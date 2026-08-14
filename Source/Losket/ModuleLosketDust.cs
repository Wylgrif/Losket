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

		// --- Etat persistant ---

		[KSPField(isPersistant = true)] public float dose;
		[KSPField(isPersistant = true)] public Vector3 dirAccum = Vector3.zero;
		[KSPField(isPersistant = true)] public Vector3 colorAccum = Vector3.zero;

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

		public override void OnStart(StartState state)
		{
			if (!HighLogic.LoadedSceneIsFlight) {
				return;
			}
			shader = LosketBootstrap.ShadersLoaded
				? LosketBootstrap.GetShader("Losket/BurnOverlay")
				: null;
			owner = part.FindModuleImplementing<ModuleLosketBurn>();

			// Meme garantie que la brulure : pas de tir depuis le pas de tir
			// avec de la poussiere d'une vie anterieure.
			if (vessel != null && vessel.situation == Vessel.Situations.PRELAUNCH) {
				dose = 0f;
				dirAccum = Vector3.zero;
				colorAccum = Vector3.zero;
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

			var ddose = rate * proximity * TimeWarp.fixedDeltaTime;
			if (ddose <= 1e-6f) {
				return;
			}

			// La poussiere arrive du sol : cote frappe = le bas local.
			var downWorld = -(part.transform.position - vessel.mainBody.position).normalized;
			var downPart = part.transform.InverseTransformDirection(downWorld);

			dose += ddose;
			dirAccum += downPart * ddose;
			var ground = source.GroundColor;
			colorAccum += new Vector3(ground.r, ground.g, ground.b) * ddose;
		}

		private void LateUpdate()
		{
			if (!HighLogic.LoadedSceneIsFlight) {
				return;
			}

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

			if (rig == null) {
				if (shader == null) {
					return;
				}
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
			p.DirPower = 0.8f;
			p.Spread = 0f;                           // depot fige : pas de gradient au rendu
			p.Sharpness = 1.2f;
			p.NoiseScale = 5f;
			p.DepositColor = dustColor;
			// Rendue apres la brulure : la poussiere se depose par-dessus.
			p.RenderQueue = 2101;

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
