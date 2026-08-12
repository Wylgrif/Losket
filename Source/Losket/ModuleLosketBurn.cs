using System;
using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Accumulation de brulure en vol.
	///
	/// Le flux thermique convectif suit la loi de Sutton-Graves, q = k·sqrt(rho)·v^3 :
	/// le cube de la vitesse garantit qu'un vol lent n'accumule jamais rien (a
	/// 5 km/h le flux est ~10^9 fois plus faible qu'a 7 km/s), sans aucun seuil
	/// de vitesse arbitraire.
	///
	/// Etat par piece : une dose scalaire (integrale saturante -> noircissement),
	/// un vecteur d'accumulation directionnelle en espace piece (moyenne ponderee
	/// des directions de flux : rentree stable = brulure orientee, vaisseau qui
	/// tumble = vecteur qui s'annule = brulure uniforme, ce qui est physiquement
	/// correct), et la temperature de peau maximale atteinte (-> LUT de revenu).
	/// Le tout persiste dans le .sfs.
	/// </summary>
	public class ModuleLosketBurn : PartModule
	{
		// --- Reglages, surchargables par ModuleManager (pas d'UI) ---

		/// <summary>Constante de Sutton-Graves simplifiee (W/m2 par sqrt(kg/m3)·(m/s)^3).</summary>
		[KSPField] public float suttonGravesK = 1.83e-4f;

		/// <summary>Flux (W/m2) en dessous duquel rien ne s'accumule.</summary>
		[KSPField] public float fluxThreshold = 5000f;

		/// <summary>Dose (J/m2) donnant ~63 % de noircissement.</summary>
		[KSPField] public float doseScale = 2e7f;

		/// <summary>Temperature de peau (K) ou le revenu commence.</summary>
		[KSPField] public float temperMin = 400f;

		/// <summary>Temperature de peau (K) ou le revenu sature.</summary>
		[KSPField] public float temperMax = 650f;

		// --- Etat persistant ---

		[KSPField(isPersistant = true)] public float dose;
		[KSPField(isPersistant = true)] public float peakSkinTemp;
		[KSPField(isPersistant = true)] public Vector3 dirAccum = Vector3.zero;

		[KSPField(guiActive = true, guiName = "Brulure accumulee", guiFormat = "P0",
			groupName = "LosketBurn", groupDisplayName = "Losket")]
		public float burnDisplay;

		private LosketOverlayRig rig;
		private Shader shader;

		/// <summary>Noircissement 0..1 derive de la dose (integrale saturante).</summary>
		private float BurnMag
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
		}

		private void FixedUpdate()
		{
			if (!HighLogic.LoadedSceneIsFlight || vessel == null || part.packed) {
				return;
			}

			// Suivi du pic de temperature de peau, meme hors accumulation de
			// suie : le bleu de chauffe peut apparaitre sans noircissement.
			var skin = (float)part.skinTemperature;
			if (skin > peakSkinTemp) {
				peakSkinTemp = skin;
			}

			var rho = vessel.atmDensity;
			var v = vessel.srfSpeed;
			if (rho <= 0 || v < 200) {
				// En dessous de 200 m/s le flux est negligeable quel que soit rho ;
				// on s'epargne le calcul.
				return;
			}

			var q = suttonGravesK * Math.Sqrt(rho) * v * v * v;
			var heating = q - fluxThreshold;
			if (heating <= 0) {
				return;
			}

			var dq = (float)(heating * TimeWarp.fixedDeltaTime);

			// Le cote frappe par l'air est le cote prograde (surface).
			var flowWorld = ((Vector3)vessel.srf_velocity).normalized;
			var flowPart = part.transform.InverseTransformDirection(flowWorld);

			dirAccum += flowPart * dq;
			dose += dq;
		}

		private void LateUpdate()
		{
			if (!HighLogic.LoadedSceneIsFlight) {
				return;
			}

			var mag = BurnMag;
			burnDisplay = mag;

			// Pas d'overlay tant que la piece est propre : cout nul pour les
			// vaisseaux qui ne rentrent pas.
			if (mag < 0.02f) {
				return;
			}

			if (rig == null) {
				if (shader == null) {
					return;
				}
				rig = LosketOverlayRig.Create(part, shader,
					part.partInfo.name + "#" + GetInstanceID() + " (vol)");
			}

			// Direction moyenne du flux. Sa longueur relative mesure la
			// stabilite de la rentree : 1 = orientation constante, 0 = tumble.
			// Un tumble donne une brulure uniforme (enveloppement max).
			var dirStrength = dose > 0f ? dirAccum.magnitude / dose : 0f;
			var flowPart = dirAccum.sqrMagnitude > 1e-6f
				? dirAccum.normalized
				: Vector3.down;

			var p = BurnParams.Defaults();
			p.WorldFlowDir = part.transform.TransformDirection(flowPart);
			p.BurnMag = mag;
			p.PeakTemp = Mathf.Clamp01((peakSkinTemp - temperMin) / (temperMax - temperMin));
			p.Wrap = Mathf.Lerp(2f, 1.3f, dirStrength);
			p.DirPower = Mathf.Lerp(0.5f, 1.5f, dirStrength);

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
