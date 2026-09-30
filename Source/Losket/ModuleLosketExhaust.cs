using System.Collections.Generic;
using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Marquage thermique par les panaches de moteurs : suie et bleu de chauffe
	/// la ou un jet a frappe la piece, et revenu de la tuyere par son propre
	/// fonctionnement.
	///
	/// Le module ne detecte rien lui-meme : LosketPlumeField parcourt les
	/// moteurs allumes et appelle Deposit() sur les pieces touchees. Ici ne
	/// vivent que l'etat persistant et son rendu.
	///
	/// Etat par piece, tout en espace piece et fige au moment du depot :
	///  - une dose saturante (noircissement) ;
	///  - le cote moyen d'ou venait le jet (faces exposees) et le sens moyen
	///    des gaz a l'impact (sens de la trainee) ;
	///  - le point d'impact moyen et son second moment, d'ou sortent le centre
	///    et le rayon de la tache. Plusieurs moteurs, ou un moteur a plusieurs
	///    tuyeres, fusionnent en une seule tache plus large ;
	///  - le flux de pointe (teinte du revenu) ;
	///  - la part de la dose venue du moteur de la piece elle-meme : une
	///    tuyere bleuit plus qu'elle ne noircit.
	///
	/// Le rendu ne lit que cet etat : ni la position ni l'orientation actuelles
	/// du vaisseau n'y entrent (invariant du depot fige, comme la poussiere).
	/// </summary>
	public class ModuleLosketExhaust : PartModule
	{
		// --- Reglages, surchargables par ModuleManager (pas d'UI) ---

		/// <summary>Secondes d'exposition a pleine intensite (sortie de tuyere)
		/// donnant ~63 % de recouvrement.</summary>
		[KSPField] public float doseScale = 4f;

		/// <summary>Vitesse de marquage de la tuyere par son propre moteur, a
		/// pleine poussee : 0.2 donne ~63 % apres 20 s de fonctionnement.</summary>
		[KSPField] public float selfRate = 0.2f;

		/// <summary>
		/// Sensibilite des pieces a moteur, toutes sources confondues (leur
		/// propre jet comme celui d'un autre moteur). Un moteur est construit
		/// pour la chaleur : a 0.1 il se marque dix fois moins vite qu'une
		/// piece ordinaire, soit ~63 % apres 200 s a pleine poussee.
		/// </summary>
		[KSPField] public float engineSensitivity = 0.1f;

		/// <summary>
		/// Faux (defaut) : la teinte du revenu vient du flux de pointe, pour
		/// une tuyere comme pour toute autre piece ; seule la sensibilite
		/// ralentit les moteurs. Vrai : sur une tuyere marquee par elle-meme,
		/// la teinte remonte le nuancier avec l'usure (paille, brun, pourpre,
		/// bleu) au lieu d'apparaitre directement dans sa couleur finale.
		/// Essaye puis ecarte ; garde pour comparer.
		/// </summary>
		[KSPField] public bool progressiveSelfTint = false;

		/// <summary>Part de suie sur une tuyere marquee par elle-meme. Le reste
		/// laisse voir le bleu de chauffe.</summary>
		[KSPField] public float selfSootGain = 0.3f;

		/// <summary>Visibilite du revenu sur une tuyere marquee par elle-meme,
		/// quel que soit le style de la piece. A 0.4 l'opacite plafonne vers
		/// 35 % : une patine, pas une peinture.</summary>
		[KSPField] public float selfTemperGain = 0.4f;

		/// <summary>Coupe du bord de la tache d'une tuyere marquee par
		/// elle-meme : la teinte s'arrete a environ un rayon du point chaud au
		/// lieu de baver sur le bati du moteur.</summary>
		[KSPField] public float selfSpotCut = 0.35f;

		/// <summary>Deformation du bruit des marques de panache (0 = grille
		/// d'origine, visible en "pixels" apres seuillage).</summary>
		[KSPField] public float noiseWarp = 1f;

		/// <summary>Etirement de la tache vers l'aval du jet.</summary>
		[KSPField] public float spotStretch = 3f;

		// --- Etat persistant ---

		[KSPField(isPersistant = true)] public float dose;
		[KSPField(isPersistant = true)] public float selfDose;
		[KSPField(isPersistant = true)] public float peakFlux;
		[KSPField(isPersistant = true)] public Vector3 dirAccum = Vector3.zero;
		[KSPField(isPersistant = true)] public Vector3 flowAccum = Vector3.zero;
		[KSPField(isPersistant = true)] public Vector3 posAccum = Vector3.zero;

		/// <summary>Somme ponderee de |position|^2 + rayon^2 : la variance qui
		/// en sort donne le rayon de la tache.</summary>
		[KSPField(isPersistant = true)] public float spreadAccum;

		// --- Enveloppe de depot (pieces deployables, voir LosketEnvelope) ---

		[KSPField(isPersistant = true)] public int envState;
		[KSPField(isPersistant = true)] public Vector3 envMin = Vector3.zero;
		[KSPField(isPersistant = true)] public Vector3 envMax = Vector3.zero;

		private LosketStowage stowage;
		private int envTick;

		[KSPField(guiActive = true, guiName = "#LOC_Losket_ExhaustAccum", guiFormat = "P0",
			groupName = "Losket", groupDisplayName = "#LOC_Losket_Group")]
		public float exhaustDisplay;

		/// <summary>Tous les modules des pieces chargees en vol. Parcouru par
		/// LosketPlumeField : sources (pieces a moteur) et cibles.</summary>
		internal static readonly List<ModuleLosketExhaust> Loaded =
			new List<ModuleLosketExhaust>();

		private LosketOverlayRig rig;
		private Shader shader;
		private bool registered;

		/// <summary>Porteur du reglage "Affecte par" et du style de la piece.</summary>
		private ModuleLosketBurn owner;

		// --- Moteurs de la piece (source de panache) ---

		private readonly List<ModuleEngines> engines = new List<ModuleEngines>();
		private readonly List<bool> engineIsCold = new List<bool>();

		internal int EngineCount
		{
			get { return engines.Count; }
		}

		internal ModuleEngines Engine(int index)
		{
			return engines[index];
		}

		/// <summary>Moteur sans jet chaud (ionique : consomme de l'electricite).</summary>
		internal bool EngineIsCold(int index)
		{
			return engineIsCold[index];
		}

		// --- Geometrie de collision (cible de panache) ---

		private readonly List<Collider> colliders = new List<Collider>();
		private Vector3 localCenter;
		private float boundRadius = 1f;
		private float geometryTime = -1000f;

		/// <summary>Duree de validite du recensement des colliders (s).</summary>
		private const float GeometryLifetime = 5f;

		private float Mag
		{
			get { return 1f - Mathf.Exp(-dose / Mathf.Max(doseScale, 1e-3f)); }
		}

		public bool IsDirty
		{
			get { return dose > 0f; }
		}

		/// <summary>Le marquage suit le choix "Brulure" de la piece (c'est une
		/// marque thermique) et l'interrupteur global des reglages.</summary>
		public bool ExhaustEnabled
		{
			get { return AffectedByExhaust && LosketSettings.ExhaustMarks(); }
		}

		/// <summary>Choix de la piece seul, sans l'interrupteur global : pour
		/// les boucles qui ont deja verifie ce dernier.</summary>
		internal bool AffectedByExhaust
		{
			get { return owner == null || owner.BurnEnabled; }
		}

		// --- Nettoyage ---

		private float cleanFade = -1f;
		private float cleanDoseStart;
		private float cleanSelfStart;
		private Vector3 cleanDirStart;
		private Vector3 cleanFlowStart;
		private Vector3 cleanPosStart;
		private float cleanSpreadStart;

		public bool IsCleaning
		{
			get { return cleanFade > 0f; }
		}

		public void BeginCleanFade()
		{
			cleanFade = ModuleLosketBurn.CleanFadeTime;
			cleanDoseStart = dose;
			cleanSelfStart = selfDose;
			cleanDirStart = dirAccum;
			cleanFlowStart = flowAccum;
			cleanPosStart = posAccum;
			cleanSpreadStart = spreadAccum;
		}

		private void TickCleanFade()
		{
			if (cleanFade <= 0f) {
				return;
			}
			cleanFade -= Time.deltaTime;
			if (cleanFade <= 0f) {
				Clean();
			} else {
				// Tous les accumulateurs decroissent ensemble : le centre et le
				// rayon de la tache (rapports a la dose) ne bougent pas pendant
				// le fondu, seule l'intensite retombe.
				var f = cleanFade / ModuleLosketBurn.CleanFadeTime;
				dose = cleanDoseStart * f;
				selfDose = cleanSelfStart * f;
				dirAccum = cleanDirStart * f;
				flowAccum = cleanFlowStart * f;
				posAccum = cleanPosStart * f;
				spreadAccum = cleanSpreadStart * f;
			}
		}

		/// <summary>Remise a neuf (ingenieur en EVA, pre-lancement).</summary>
		public void Clean()
		{
			dose = 0f;
			selfDose = 0f;
			peakFlux = 0f;
			dirAccum = Vector3.zero;
			flowAccum = Vector3.zero;
			posAccum = Vector3.zero;
			spreadAccum = 0f;
			envState = LosketEnvelope.None;
		}

		public override void OnStart(StartState state)
		{
			if (!HighLogic.LoadedSceneIsFlight) {
				return;
			}
			shader = LosketBootstrap.ShadersLoaded
				? LosketBootstrap.GetShader("Losket/BurnOverlay")
				: null;
			owner = part.FindModuleImplementing<ModuleLosketBurn>();
			stowage = LosketStowage.For(part);

			if (vessel != null && vessel.situation == Vessel.Situations.PRELAUNCH) {
				Clean();
			}

			engines.Clear();
			engineIsCold.Clear();
			for (var i = 0; i < part.Modules.Count; i++) {
				// ModuleEnginesFX derive de ModuleEngines : un seul test couvre
				// les deux, et les deux modes d'un moteur bimode.
				var engine = part.Modules[i] as ModuleEngines;
				if (engine == null) {
					continue;
				}
				engines.Add(engine);
				engineIsCold.Add(UsesElectricity(engine));
			}

			if (!registered) {
				Loaded.Add(this);
				registered = true;
			}
		}

		private static bool UsesElectricity(ModuleEngines engine)
		{
			if (engine.propellants == null) {
				return false;
			}
			for (var i = 0; i < engine.propellants.Count; i++) {
				var propellant = engine.propellants[i];
				if (propellant != null && propellant.name == "ElectricCharge") {
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Depose une dose. Tout est converti en espace piece ici, une fois pour
		/// toutes : ce qui est depose ne depend plus de rien ensuite.
		/// </summary>
		/// <param name="ddose">Dose (secondes equivalentes a pleine intensite).</param>
		/// <param name="flux">Intensite du jet, 0..1, pour le flux de pointe.</param>
		/// <param name="towardSourceWorld">Cote d'ou vient le jet : les faces
		/// tournees vers cette direction sont les plus marquees.</param>
		/// <param name="flowWorld">Sens de deplacement des gaz a l'impact.</param>
		/// <param name="pointWorld">Point d'impact.</param>
		/// <param name="spotRadius">Rayon de la tache a cet endroit (m).</param>
		/// <param name="self">Vrai si le jet vient d'un moteur de cette piece.</param>
		internal void Deposit(float ddose, float flux, Vector3 towardSourceWorld,
			Vector3 flowWorld, Vector3 pointWorld, float spotRadius, bool self)
		{
			if (engines.Count > 0) {
				ddose *= Mathf.Max(0f, engineSensitivity);
			}
			if (ddose <= 1e-7f) {
				return;
			}
			var t = part.transform;
			var dirPart = t.InverseTransformDirection(towardSourceWorld);
			var flowPart = t.InverseTransformDirection(flowWorld);
			var posPart = t.InverseTransformPoint(pointWorld);

			dose += ddose;
			if (self) {
				selfDose += ddose;
			}
			dirAccum += dirPart * ddose;
			flowAccum += flowPart * ddose;
			posAccum += posPart * ddose;
			spreadAccum += (posPart.sqrMagnitude + spotRadius * spotRadius) * ddose;
			LosketEnvelope.Track(part, stowage, ref envState, ref envMin, ref envMax, ref envTick);
			if (flux > peakFlux) {
				peakFlux = flux;
			}
		}

		// --- Geometrie ---

		/// <summary>Recense les colliders de la piece et sa sphere englobante,
		/// au plus une fois toutes les quelques secondes.</summary>
		internal void EnsureGeometry()
		{
			if (Time.time - geometryTime < GeometryLifetime) {
				return;
			}
			geometryTime = Time.time;
			colliders.Clear();

			var t = part.transform;
			var any = false;
			var min = Vector3.zero;
			var max = Vector3.zero;
			foreach (var c in part.GetComponentsInChildren<Collider>(false)) {
				if (c == null || !c.enabled || c.isTrigger || c is WheelCollider) {
					continue;
				}
				// Dans certaines scenes les pieces enfants sont imbriquees sous
				// le parent : ne garder que ce qui appartient a CETTE piece.
				if (c.GetComponentInParent<Part>() != part) {
					continue;
				}
				colliders.Add(c);

				var b = c.bounds;
				var center = t.InverseTransformPoint(b.center);
				var reach = Vector3.one * b.extents.magnitude;
				if (!any) {
					min = center - reach;
					max = center + reach;
					any = true;
				} else {
					min = Vector3.Min(min, center - reach);
					max = Vector3.Max(max, center + reach);
				}
			}

			if (any) {
				localCenter = (min + max) * 0.5f;
				boundRadius = (max - min).magnitude * 0.5f;
			} else {
				localCenter = Vector3.zero;
				boundRadius = 0f;
			}
		}

		internal bool HasColliders
		{
			get { return colliders.Count > 0; }
		}

		internal Vector3 WorldCenter
		{
			get { return part.transform.TransformPoint(localCenter); }
		}

		internal float BoundRadius
		{
			get { return boundRadius; }
		}

		/// <summary>Point de la surface de collision le plus proche de q.</summary>
		internal Vector3 ClosestSurfacePoint(Vector3 q)
		{
			var best = q;
			var bestSqr = float.MaxValue;
			for (var i = 0; i < colliders.Count; i++) {
				var c = colliders[i];
				if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) {
					continue;
				}
				// Collider.ClosestPoint n'accepte que les formes convexes ; pour
				// un maillage concave on se rabat sur sa boite englobante.
				var mesh = c as MeshCollider;
				var p = mesh != null && !mesh.convex
					? c.bounds.ClosestPoint(q)
					: c.ClosestPoint(q);
				var sqr = (p - q).sqrMagnitude;
				if (sqr < bestSqr) {
					bestSqr = sqr;
					best = p;
				}
			}
			return best;
		}

		// --- Rendu ---

		private void LateUpdate()
		{
			if (!HighLogic.LoadedSceneIsFlight) {
				return;
			}

			TickCleanFade();
			var mag = Mag;
			exhaustDisplay = mag;

			if (mag < 0.02f || !ExhaustEnabled) {
				if (rig != null) {
					rig.Destroy();
					rig = null;
				}
				return;
			}
			if (shader == null) {
				return;
			}

			if (rig == null ||
			    ((Time.frameCount + (GetInstanceID() & 0xFF)) % 60 == 0 && rig.IsStale(part))) {
				rig = LosketOverlayRig.Create(part, shader,
					part.partInfo.name + "#" + GetInstanceID() + " (panache)",
					LosketOverlayRig.ExhaustOverlayName);
			}

			var towardSource = dirAccum.sqrMagnitude > 1e-8f
				? dirAccum.normalized
				: Vector3.down;
			// 1 = le jet est toujours venu du meme cote, 0 = de partout.
			var dirStrength = Mathf.Clamp01(dirAccum.magnitude / dose);
			var selfShare = Mathf.Clamp01(selfDose / dose);

			// Le style de la piece (couleur du depot, nettete, echelle du bruit,
			// eclaircissement) dit comment SA surface reagit a la chaleur : il
			// vaut pour un panache comme pour une rentree.
			BurnParams p;
			var temperGain = 0.35f;
			if (owner != null) {
				owner.EnsurePatternFrame();
				p = owner.StyleParams();
				p.PartToPattern = owner.PatternMatrix;
				temperGain = Mathf.Max(temperGain, owner.temperIntensity);
			} else {
				p = BurnParams.Defaults();
			}

			p.WorldFlowDir = part.transform.TransformDirection(towardSource);
			p.BurnMag = mag;
			// Teinte du revenu : elle vient du flux de pointe. Une tuyere a
			// pleine poussee lit donc le haut du nuancier (bleu clair) ; c'est
			// engineSensitivity qui regle le temps qu'elle met a apparaitre.
			p.PeakTemp = Mathf.Clamp01(peakFlux * 1.5f);
			if (progressiveSelfTint) {
				p.PeakTemp *= Mathf.Lerp(1f, Mathf.Sqrt(mag), selfShare);
			}
			// Une tuyere marquee par elle-meme montre surtout son revenu.
			p.TemperGain = Mathf.Lerp(temperGain, selfTemperGain, selfShare);
			p.SpotCut = selfSpotCut * selfShare;
			p.NoiseWarp = noiseWarp;
			p.SootGain = Mathf.Lerp(1f, selfSootGain, selfShare);
			p.Pattern = 0f;                          // taches : pas de ligne de stagnation
			p.Streak = 1f;
			p.Spread = 0f;                           // depot fige : pas de gradient au rendu
			p.Wrap = Mathf.Lerp(2f, 1.4f, dirStrength * (1f - selfShare));
			p.DirPower = Mathf.Lerp(0.5f, 1.2f, dirStrength);
			p.RenderQueue = LosketOverlayRig.ExhaustQueue;

			var center = posAccum / dose;
			var variance = spreadAccum / dose - center.sqrMagnitude;
			p.SpotEnabled = true;
			p.SpotPosPart = center;
			p.SpotRadius = Mathf.Sqrt(Mathf.Max(variance, 0.01f));
			p.SpotFlowPart = flowAccum.sqrMagnitude > 1e-8f
				? flowAccum.normalized
				: -towardSource;
			// Autour de sa propre tuyere la chaleur diffuse sans sens
			// privilegie : pas de trainee.
			p.SpotStretch = Mathf.Lerp(Mathf.Max(1f, spotStretch), 1f, selfShare);
			if (envState == LosketEnvelope.Stowed) {
				p.UseEnvelope = true;
				p.EnvelopeMin = envMin;
				p.EnvelopeMax = envMax;
			}

			rig.Apply(p);
		}

		/// <summary>Resume de l'etat pour le diagnostic par piece.</summary>
		internal string Describe()
		{
			if (dose <= 0f) {
				return "panache : rien";
			}
			var center = posAccum / dose;
			var variance = spreadAccum / dose - center.sqrMagnitude;
			return "panache : dose=" + dose.ToString("0.00") +
			       " mag=" + Mag.ToString("0.00") +
			       " propre=" + (selfDose / dose).ToString("0.00") +
			       " fluxMax=" + peakFlux.ToString("0.00") +
			       " sensibilite=" + (engines.Count > 0 ? engineSensitivity : 1f).ToString("0.00") +
			       " tache=" + center.ToString("0.00") +
			       " rayon=" + Mathf.Sqrt(Mathf.Max(variance, 0.01f)).ToString("0.00") + "m" +
			       " colliders=" + colliders.Count +
			       " overlays=" + (rig != null ? rig.Count : 0);
		}

		private void OnDestroy()
		{
			if (registered) {
				Loaded.Remove(this);
				registered = false;
			}
			if (rig != null) {
				rig.Destroy();
				rig = null;
			}
		}
	}
}
