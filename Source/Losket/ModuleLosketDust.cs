using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Accumulation de poussiere par piece. Meme structure que la brulure : une
	/// dose saturante, un vecteur directionnel en espace piece (la poussiere
	/// arrive du sol, donc par le bas au moment du depot), et en plus une couleur
	/// accumulee - la moyenne ponderee des couleurs de sol rencontrees, pour
	/// qu'un atterrisseur qui a touche la Mun puis Minmus porte un melange des
	/// deux. Le tout persiste dans le .sfs.
	/// </summary>
	public class ModuleLosketDust : PartModule
	{
		// --- Reglages, surchargables par ModuleManager (pas d'UI) ---

		/// <summary>Dose donnant ~63 % de recouvrement (unites de KickRate*s).</summary>
		[KSPField] public float doseScale = 150f;

		/// <summary>Lavage a l'arret : constante de temps (s) d'une piece
		/// entierement immergee et immobile dans l'eau.</summary>
		[KSPField] public float soakTime = 30f;

		/// <summary>Lavage en mouvement : distance (m) parcourue dans l'eau qui
		/// divise le recouvrement par e. Une coque a 10 m/s se lave en ~3 s.</summary>
		[KSPField] public float washLength = 25f;

		/// <summary>Arrachage par le vent relatif : pression dynamique (kPa)
		/// en dessous de laquelle la poussiere tient. ~10 kPa = 130 m/s au
		/// niveau de la mer de Kerbin ; un rover ou un atterrisseur sur Duna
		/// n'y arrive jamais.</summary>
		[KSPField] public float airMinQ = 10f;

		/// <summary>Arrachage par le vent relatif : constante de temps (s) a
		/// une pression dynamique double du seuil. Elle raccourcit a mesure que
		/// la pression monte : ~20 s a 40 kPa (max-Q d'une fusee).</summary>
		[KSPField] public float airTime = 60f;

		// --- Etat persistant ---

		[KSPField(isPersistant = true)] public float dose;
		[KSPField(isPersistant = true)] public Vector3 dirAccum = Vector3.zero;
		[KSPField(isPersistant = true)] public Vector3 colorAccum = Vector3.zero;

		// --- Enveloppe de depot (pieces deployables, voir LosketEnvelope) ---

		[KSPField(isPersistant = true)] public int envState;
		[KSPField(isPersistant = true)] public Vector3 envMin = Vector3.zero;
		[KSPField(isPersistant = true)] public Vector3 envMax = Vector3.zero;

		private LosketStowage stowage;
		private int envTick;

		/// <summary>Part de la dose deposee en gerbe rasante (vide). Un
		/// atterrisseur qui a connu la Mun puis Kerbin garde le melange.</summary>
		[KSPField(isPersistant = true)] public float ringAccum;

		[KSPField(guiActive = true, guiName = "#LOC_Losket_DustAccum", guiFormat = "P0",
			groupName = "Losket", groupDisplayName = "#LOC_Losket_Group")]
		public float dustDisplay;

		private LosketOverlayRig rig;
		private Shader shader;
		private LosketDustSource source;
		private Vessel sourceVessel;

		/// <summary>Porteur du reglage "Affecte par" de la piece.</summary>
		private ModuleLosketBurn owner;

		private float DustMag
		{
			get { return 1f - Mathf.Exp(-dose / doseScale); }
		}

		public bool IsDirty
		{
			get { return dose > 0f; }
		}


		/// <summary>Duree du fondu de nettoyage (s) : le depot s'efface
		/// pendant que les particules s'echappent, au lieu de disparaitre net.</summary>
		public const float CleanFadeTime = 1.5f;

		private float cleanFade = -1f;
		private float cleanDoseStart;
		private Vector3 cleanDirStart;
		private Vector3 cleanColorStart;
		private float cleanRingStart;

		public bool IsCleaning
		{
			get { return cleanFade > 0f; }
		}

		public void BeginCleanFade()
		{
			cleanFade = CleanFadeTime;
			cleanDoseStart = dose;
			cleanDirStart = dirAccum;
			cleanColorStart = colorAccum;
			cleanRingStart = ringAccum;
		}

		/// <summary>Fait avancer le fondu ; la dose retombe lineairement puis
		/// Clean() finalise l'etat persistant.</summary>
		private void TickCleanFade()
		{
			if (cleanFade <= 0f) {
				return;
			}
			cleanFade -= Time.deltaTime;
			if (cleanFade <= 0f) {
				Clean();
			} else {
				// Tous les accumulateurs decroissent ensemble : les rapports
				// colorAccum/dose (teinte) et ringAccum/dose (anneau) restent
				// constants. Ne baisser que la dose ferait exploser la teinte
				// vers le blanc.
				var f = cleanFade / CleanFadeTime;
				dose = cleanDoseStart * f;
				dirAccum = cleanDirStart * f;
				colorAccum = cleanColorStart * f;
				ringAccum = cleanRingStart * f;
			}
		}

		/// <summary>Remise a neuf (nettoyage par un ingenieur en EVA, ou
		/// pre-lancement). L'overlay disparait au prochain LateUpdate.</summary>
		public void Clean()
		{
			dose = 0f;
			dirAccum = Vector3.zero;
			colorAccum = Vector3.zero;
			ringAccum = 0f;
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

			// Meme garantie que la brulure : pas de tir depuis le pas de tir
			// avec de la poussiere d'une vie anterieure.
			if (vessel != null && vessel.situation == Vessel.Situations.PRELAUNCH) {
				Clean();
			}
		}

		private bool DustEnabled
		{
			get { return owner == null || owner.DustEnabled; }
		}

		private void FixedUpdate()
		{
			if (!HighLogic.LoadedSceneIsFlight || vessel == null || part.packed) {
				return;
			}
			if (!DustEnabled) {
				return;
			}

			// A l'abri d'une coiffe ou d'une soute, pas de poussiere.
			if (part.ShieldedFromAirstream) {
				return;
			}

			// L'eau et le vent relatif enlevent la poussiere (pas la brulure).
			if (dose > 0f && !IsCleaning) {
				var fade = WashRate() + AirRate();
				if (fade > 0f) {
					Fade(fade);
				}
			}

			if (source == null || sourceVessel != vessel) {
				// Le vaisseau peut changer (docking, separation) : on re-resout.
				source = vessel.FindVesselModuleImplementing<LosketDustSource>();
				sourceVessel = vessel;
				if (source == null) {
					return;
				}
			}

			var rate = source.KickRate;
			if (rate <= 0f) {
				return;
			}

			// Systeme de proximite : la dose depend de la hauteur de CETTE piece
			// au-dessus du sol au moment du depot. Une capsule au sommet d'une
			// fusee recoit une fraction de ce que recoit la tuyere. La hauteur
			// etant continue dans l'espace, le degrade le long du vaisseau est
			// continu entre pieces par construction - et comme la dose est
			// figee une fois deposee, ni le vol ni le staging ne la modifient.
			var h = Mathf.Max(0f, source.RadarAlt +
				Vector3.Dot(part.transform.position - source.VesselRef, source.UpWorld));
			var proximity = Mathf.Exp(-h / source.ScaleHeight);

			// Facteur global des reglages de partie (x0.1 a x10), applique au
			// depot et non au rendu : ce qui est depose reste depose.
			var ddose = rate * proximity * LosketSettings.DustRate() * TimeWarp.fixedDeltaTime;
			if (ddose <= 1e-6f) {
				return;
			}

			// La poussiere arrive du sol : cote frappe = le bas local.
			var downWorld = -(part.transform.position - vessel.mainBody.position).normalized;
			var downPart = part.transform.InverseTransformDirection(downWorld);

			dose += ddose;
			dirAccum += downPart * ddose;
			ringAccum += source.RingFactor * ddose;
			LosketEnvelope.Track(part, stowage, ref envState, ref envMin, ref envMax, ref envTick);
			var ground = source.GroundColor;
			colorAccum += new Vector3(ground.r, ground.g, ground.b) * ddose;
		}

		/// <summary>
		/// L'eau lave la poussiere (pas la brulure). Le taux (1/s) croit avec
		/// la part immergee de la piece et avec sa vitesse dans l'eau :
		/// immobile, elle trempe lentement (soakTime) ; en mouvement, le
		/// courant arrache la poussiere en proportion de la distance parcourue
		/// (washLength). Toute la piece s'estompe d'un bloc, sans distinguer la
		/// partie immergee - approximation assumee.
		/// </summary>
		private float WashRate()
		{
			var body = vessel.mainBody;
			if (body == null || !body.ocean) {
				return 0f;
			}

			var pos = part.transform.position;
			float wet;
			float speed;
			if (part.rb != null && part.physicalSignificance == Part.PhysicalSignificance.FULL) {
				// Calcule par KSP pour la flottabilite, a chaque tick physique.
				wet = (float)part.submergedPortion;
				// Vitesse propre de la piece par rapport a l'eau (qui tourne
				// avec l'astre) : une helice immergee se lave plus vite que la coque.
				var srfVel = (Vector3d)part.rb.velocity + Krakensbane.GetFrameVelocity()
					- body.getRFrmVel(pos);
				speed = (float)srfVel.magnitude;
			} else {
				// Pieces sans physique : pas de flottabilite calculee, on se
				// rabat sur l'altitude de leur origine.
				wet = FlightGlobals.getAltitudeAtPos(pos, body) < 0.0 ? 1f : 0f;
				speed = (float)vessel.srfSpeed;
			}
			if (wet < 0.01f) {
				return 0f;
			}
			return wet * (1f / Mathf.Max(soakTime, 0.1f) + speed / Mathf.Max(washLength, 0.1f));
		}

		/// <summary>
		/// Le vent relatif arrache la poussiere quand la pression dynamique
		/// depasse airMinQ : un avion rapide ou une rentree se nettoient avec
		/// le temps, un rover ou une descente sous moteurs jamais. Le taux
		/// (1/s) croit lineairement au-dela du seuil. La pression est celle du
		/// vaisseau, et toute la piece s'estompe d'un bloc, face au vent ou
		/// non - meme approximation que le lavage. Les pieces sous coiffe ou en
		/// soute sont deja ecartees par l'appelant.
		/// </summary>
		private float AirRate()
		{
			var minQ = Mathf.Max(airMinQ, 0.1f);
			var q = (float)vessel.dynamicPressurekPa;
			if (q <= minQ) {
				return 0f;
			}
			return (q - minQ) / minQ / Mathf.Max(airTime, 0.1f);
		}

		/// <summary>Fait decroitre le recouvrement au taux donne (1/s).</summary>
		private void Fade(float rate)
		{
			// On fait decroitre le recouvrement VISIBLE, pas la dose : celle-ci
			// sature (1 - e^-dose/doseScale), et une piece tres sale resterait
			// longtemps inchangee a l'ecran avant de commencer a palir.
			var mag = Mathf.Min(DustMag, 0.999f);
			var newMag = mag * Mathf.Exp(-rate * TimeWarp.fixedDeltaTime);
			if (newMag < 0.02f) {
				// Sous le seuil d'affichage de l'overlay : propre.
				Clean();
				return;
			}
			// Facteur commun a tous les accumulateurs : la teinte (colorAccum /
			// dose), l'orientation et la part en anneau restent inchangees.
			var k = -doseScale * Mathf.Log(1f - newMag) / dose;
			dose *= k;
			dirAccum *= k;
			colorAccum *= k;
			ringAccum *= k;
		}

		private void LateUpdate()
		{
			if (!HighLogic.LoadedSceneIsFlight) {
				return;
			}

			TickCleanFade();
			var mag = DustMag;
			dustDisplay = mag;

			// Pas d'overlay tant que la piece est propre ou l'effet desactive.
			if (!DustEnabled || mag < 0.02f) {
				if (rig != null) {
					rig.Destroy();
					rig = null;
				}
				return;
			}

			if (shader == null) {
				return;
			}
			// Meme recensement que la brulure : les coiffes procedurales et
			// autres maillages generes au runtime sont rattrapes des qu'ils
			// apparaissent, et les panneaux largues partent avec leur poussiere.
			if (rig == null ||
			    ((Time.frameCount + (GetInstanceID() & 0xFF)) % 60 == 0 && rig.IsStale(part))) {
				rig = LosketOverlayRig.Create(part, shader,
					part.partInfo.name + "#" + GetInstanceID() + " (poussiere)",
					LosketOverlayRig.DustOverlayName);
			}

			var downPart = dirAccum.sqrMagnitude > 1e-6f
				? dirAccum.normalized
				: Vector3.down;
			var tint = dose > 0f ? colorAccum / dose : new Vector3(0.5f, 0.45f, 0.4f);
			var dustColor = new Color(tint.x, tint.y, tint.z);

			// Aux forts recouvrements, la couche est epaisse : on sature la
			// teinte pour qu'elle domine visuellement (sans cela, un vaisseau
			// couvert de poussiere de Duna reste a peine rougeatre).
			var thickness = Mathf.InverseLerp(0.85f, 1f, mag);
			if (thickness > 0f) {
				float h, s, v;
				Color.RGBToHSV(dustColor, out h, out s, out v);
				s = Mathf.Min(1f, s * (1f + 0.7f * thickness));
				v = Mathf.Min(1f, v * (1f + 0.12f * thickness));
				dustColor = Color.HSVToRGB(h, s, v);
			}

			var p = BurnParams.Defaults();
			if (owner != null) {
				owner.EnsurePatternFrame();
				p.PartToPattern = owner.PatternMatrix;
			}
			p.WorldFlowDir = part.transform.TransformDirection(downPart);

			// Aucun gradient positionnel au rendu (_Spread = 0 le desactive) :
			// la poussiere est un depot fige, son rendu ne doit dependre que de
			// l'etat accumule, jamais de la position ou de l'orientation
			// actuelles du vaisseau - c'etait la cause du scintillement et de la
			// capsule qui se salissait au staging. Le degrade macro vient des
			// doses par piece, calculees a la source.
			p.BurnMag = mag;
			p.PeakTemp = 0f;                        // la poussiere ne chauffe pas
			p.Pattern = 0f;                          // toujours en taches
			p.Wrap = 1.7f;                           // elle se faufile partout
			// Fraction du depot recue en gerbe rasante -> masque en anneau.
			p.RingMask = dose > 0f ? Mathf.Clamp01(ringAccum / dose) : 0f;
			p.DirPower = 0.8f;
			p.Spread = 0f;                           // depot fige : pas de gradient au rendu
			p.Sharpness = 1.2f;
			p.NoiseScale = 5f;
			p.DepositColor = dustColor;
			// Rendue apres la brulure et les marques de panache : la poussiere
			// se depose par-dessus.
			p.RenderQueue = LosketOverlayRig.DustQueue;
			if (envState == LosketEnvelope.Stowed) {
				p.UseEnvelope = true;
				p.EnvelopeMin = envMin;
				p.EnvelopeMax = envMax;
			}

			rig.Apply(p);
		}

		private void OnDestroy()
		{
			if (rig != null) {
				rig.Destroy();
				rig = null;
			}
		}
	}
}
