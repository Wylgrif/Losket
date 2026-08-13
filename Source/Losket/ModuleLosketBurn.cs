using System;
using KSP.Localization;
using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Accumulation de brulure en vol, et porteur du style visuel de la piece.
	///
	/// Le flux thermique convectif suit la loi de Sutton-Graves, q = k*sqrt(rho)*v^3 :
	/// le cube de la vitesse garantit qu'un vol lent n'accumule jamais rien, sans
	/// seuil de vitesse arbitraire.
	///
	/// Etat par piece : une dose scalaire (integrale saturante -> noircissement),
	/// un vecteur d'accumulation directionnelle en espace piece (rentree stable =
	/// brulure orientee, tumble = brulure uniforme), et la temperature de peau
	/// maximale atteinte (-> LUT de revenu). Le tout persiste dans le .sfs.
	///
	/// Interface (cahier des charges) : le PAW de l'editeur porte "Affecte par"
	/// (rien / brulure / poussiere / les deux, defaut pris dans les reglages de
	/// partie), un choix de preregle, et un bouton ouvrant la fenetre de
	/// reglages avances (LosketStyleWindow). La teinte du revenu vient toujours
	/// de la LUT : le seul controle utilisateur est son intensite.
	/// </summary>
	public class ModuleLosketBurn : PartModule
	{
		private const string Group = "Losket";
		private const string GroupTitle = "#LOC_Losket_Group";

		// --- Reglages physiques, surchargables par ModuleManager (pas d'UI) ---

		/// <summary>Constante de Sutton-Graves simplifiee (W/m2 par sqrt(kg/m3)*(m/s)^3).</summary>
		[KSPField] public float suttonGravesK = 1.83e-4f;

		/// <summary>Flux (W/m2) en dessous duquel rien ne s'accumule.</summary>
		[KSPField] public float fluxThreshold = 5000f;

		/// <summary>Dose (J/m2) donnant ~63 % de noircissement.</summary>
		[KSPField] public float doseScale = 2e7f;

		/// <summary>Temperature de peau (K) ou le revenu commence.</summary>
		[KSPField] public float temperMin = 400f;

		/// <summary>Temperature de peau (K) ou le revenu sature.</summary>
		[KSPField] public float temperMax = 650f;

		// --- Etat physique persistant ---

		[KSPField(isPersistant = true)] public float dose;
		[KSPField(isPersistant = true)] public float peakSkinTemp;
		[KSPField(isPersistant = true)] public Vector3 dirAccum = Vector3.zero;

		// --- Interface utilisateur ---

		/// <summary>Cle stable, vide = suivre le defaut des reglages de partie.</summary>
		[KSPField(isPersistant = true, guiActiveEditor = true,
			guiName = "#LOC_Losket_AffectedBy",
			groupName = Group, groupDisplayName = GroupTitle),
		 UI_ChooseOption]
		public string affectedBy = "";

		[KSPField(isPersistant = true, guiActiveEditor = true,
			guiName = "#LOC_Losket_Preset",
			groupName = Group, groupDisplayName = GroupTitle),
		 UI_ChooseOption]
		public string presetName = LosketKeys.PresetSoot;

		[KSPField(guiActive = true, guiName = "#LOC_Losket_BurnAccum", guiFormat = "P0",
			groupName = Group, groupDisplayName = GroupTitle)]
		public float burnDisplay;

		// --- Style persistant, edite par la fenetre de reglages ---

		[KSPField(isPersistant = true)] public float sootR = 0.05f;
		[KSPField(isPersistant = true)] public float sootG = 0.048f;
		[KSPField(isPersistant = true)] public float sootB = 0.045f;
		[KSPField(isPersistant = true)] public float temperIntensity = 0.15f;
		[KSPField(isPersistant = true)] public float sharpness = 1.3f;
		[KSPField(isPersistant = true)] public float streak = 1f;
		[KSPField(isPersistant = true)] public float noiseScale = 3f;
		[KSPField(isPersistant = true)] public float pattern;
		[KSPField(isPersistant = true)] public float bleach;

		private LosketOverlayRig rig;
		private Shader shader;
		private bool editorPreview;

		public bool BurnEnabled
		{
			get {
				return affectedBy == LosketKeys.AffectedBurn ||
				       affectedBy == LosketKeys.AffectedBoth;
			}
		}

		public bool DustEnabled
		{
			get {
				return affectedBy == LosketKeys.AffectedDust ||
				       affectedBy == LosketKeys.AffectedBoth;
			}
		}

		/// <summary>Noircissement 0..1 derive de la dose (integrale saturante).</summary>
		private float BurnMag
		{
			get { return 1f - Mathf.Exp(-dose / doseScale); }
		}

		public override void OnStart(StartState state)
		{
			// Sauvegardes anterieures a la localisation : les libelles francais
			// etaient stockes en clair.
			affectedBy = LosketKeys.MigrateAffected(affectedBy);
			presetName = LosketKeys.MigratePreset(presetName);

			// Les pieces posees avant l'installation du mod (ou jamais editees)
			// suivent le defaut global des reglages de partie.
			if (string.IsNullOrEmpty(affectedBy)) {
				affectedBy = LosketSettings.DefaultAffectedKey();
			}

			shader = LosketBootstrap.ShadersLoaded
				? LosketBootstrap.GetShader("Losket/BurnOverlay")
				: null;

			if (HighLogic.LoadedSceneIsFlight) {
				// Remise a zero au pre-lancement : un vaisseau qui part du pas de
				// tir est toujours propre. C'est aussi la garantie "recuperation
				// = nettoyage" du cahier des charges, quel que soit le chemin par
				// lequel le vaisseau revient dans l'editeur (inventaire, mods de
				// construction, .craft sauve en vol).
				if (vessel != null && vessel.situation == Vessel.Situations.PRELAUNCH) {
					dose = 0f;
					peakSkinTemp = 0f;
					dirAccum = Vector3.zero;
				}
			}

			if (HighLogic.LoadedSceneIsEditor) {
				// Les libelles traduits sont poses ici et non dans l'attribut :
				// la langue n'est connue qu'au chargement du jeu.
				var affected = Fields["affectedBy"].uiControlEditor as UI_ChooseOption;
				if (affected != null) {
					affected.options = LosketKeys.AffectedOptions;
					affected.display = LosketKeys.AffectedDisplay();
				}

				var preset = Fields["presetName"].uiControlEditor as UI_ChooseOption;
				if (preset != null) {
					preset.options = LosketKeys.PresetOptions;
					preset.display = LosketKeys.PresetDisplay();
					preset.onFieldChanged = OnPresetChanged;
				}

				Events["ToggleEditorPreview"].guiName =
					Localizer.Format("#LOC_Losket_PreviewShow");
			}
		}

		private void OnPresetChanged(BaseField field, object oldValue)
		{
			if (presetName != LosketKeys.PresetCustom) {
				ApplyPreset(presetName);
			}
		}

		/// <summary>Applique un preregle livre avec le mod.</summary>
		public void ApplyPreset(string name)
		{
			switch (name) {
				case LosketKeys.PresetSoot:
					// Matte, noire, bords un peu flous, pas d'irisation.
					sootR = 0.05f; sootG = 0.048f; sootB = 0.045f;
					temperIntensity = 0.15f; sharpness = 1.3f; streak = 1f;
					noiseScale = 3f; pattern = 0f; bleach = 0f;
					break;
				case LosketKeys.PresetMetal:
					// Gris clair, plus net, plein bleu de chauffe.
					sootR = 0.35f; sootG = 0.35f; sootB = 0.36f;
					temperIntensity = 1f; sharpness = 2.5f; streak = 1.5f;
					noiseScale = 4f; pattern = 0f; bleach = 0.15f;
					break;
				case LosketKeys.PresetStreaks:
					// Trainees blanchies type Starship.
					sootR = 0.5f; sootG = 0.48f; sootB = 0.45f;
					temperIntensity = 0.3f; sharpness = 4.5f; streak = 10f;
					noiseScale = 6f; pattern = 1f; bleach = 0.85f;
					break;
				default:
					return;
			}
			presetName = name;
		}

		/// <summary>Appele par la fenetre quand l'utilisateur touche un curseur.</summary>
		public void MarkCustomised()
		{
			presetName = LosketKeys.PresetCustom;
		}

		[KSPEvent(guiActiveEditor = true, guiName = "#LOC_Losket_Advanced",
			groupName = Group, groupDisplayName = GroupTitle)]
		public void OpenStyleWindow()
		{
			LosketStyleWindow.Open(this);
		}

		[KSPEvent(guiActiveEditor = true, guiName = "#LOC_Losket_PreviewShow",
			groupName = Group, groupDisplayName = GroupTitle)]
		public void ToggleEditorPreview()
		{
			editorPreview = !editorPreview;
			Events["ToggleEditorPreview"].guiName = Localizer.Format(editorPreview
				? "#LOC_Losket_PreviewHide"
				: "#LOC_Losket_PreviewShow");
			if (!editorPreview && rig != null) {
				rig.Destroy();
				rig = null;
			}
		}

		private void FixedUpdate()
		{
			if (!HighLogic.LoadedSceneIsFlight || vessel == null || part.packed) {
				return;
			}
			if (!BurnEnabled) {
				return;
			}

			// Suivi du pic de temperature de peau, meme hors accumulation de
			// suie : le bleu de chauffe peut apparaitre sans noircissement.
			var skin = (float)part.skinTemperature;
			if (skin > peakSkinTemp) {
				peakSkinTemp = skin;
			}

			// A l'abri d'une coiffe ou d'une soute, pas de flux direct : pas de
			// suie. On s'appuie sur l'occlusion aerodynamique du jeu lui-meme.
			if (part.ShieldedFromAirstream) {
				return;
			}

			var rho = vessel.atmDensity;
			var v = vessel.srfSpeed;
			if (rho <= 0 || v < 200) {
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
			if (HighLogic.LoadedSceneIsEditor) {
				EditorPreviewUpdate();
				return;
			}
			if (!HighLogic.LoadedSceneIsFlight) {
				return;
			}

			var mag = BurnMag;
			burnDisplay = mag;

			if (!BurnEnabled || mag < 0.02f) {
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
					part.partInfo.name + "#" + GetInstanceID() + " (vol)");
			}

			// Direction moyenne du flux. Sa longueur relative mesure la
			// stabilite de la rentree : 1 = orientation constante, 0 = tumble.
			var dirStrength = dose > 0f ? dirAccum.magnitude / dose : 0f;
			var flowPart = dirAccum.sqrMagnitude > 1e-6f
				? dirAccum.normalized
				: Vector3.down;

			var p = StyleParams();
			p.WorldFlowDir = part.transform.TransformDirection(flowPart);
			p.BurnMag = mag;
			p.PeakTemp = temperIntensity *
				Mathf.Clamp01((peakSkinTemp - temperMin) / (temperMax - temperMin));
			p.Wrap = Mathf.Lerp(2f, 1.3f, dirStrength);
			p.DirPower = Mathf.Lerp(0.5f, 1.5f, dirStrength);

			rig.Apply(p);
		}

		/// <summary>Apercu editeur : brulure representative avec le style courant.</summary>
		private void EditorPreviewUpdate()
		{
			if (!editorPreview) {
				return;
			}
			if (rig == null) {
				if (shader == null) {
					return;
				}
				rig = LosketOverlayRig.Create(part, shader,
					part.partInfo.name + "#" + GetInstanceID() + " (apercu)");
			}

			var p = StyleParams();
			p.WorldFlowDir = part.transform.TransformDirection(Vector3.down);
			p.BurnMag = 0.65f;
			p.PeakTemp = temperIntensity * 0.8f;
			p.Wrap = 1.5f;
			p.DirPower = 1.2f;
			rig.Apply(p);
		}

		/// <summary>Parametres visuels issus du style persistant de la piece.</summary>
		private BurnParams StyleParams()
		{
			var p = BurnParams.Defaults();
			p.Sharpness = sharpness;
			p.Streak = streak;
			p.NoiseScale = noiseScale;
			p.Pattern = pattern;
			p.Bleach = bleach;
			p.DepositColor = new Color(sootR, sootG, sootB);
			return p;
		}

		private void OnDestroy()
		{
			LosketStyleWindow.CloseIfTarget(this);
			if (rig != null) {
				rig.Destroy();
				rig = null;
			}
		}
	}
}
