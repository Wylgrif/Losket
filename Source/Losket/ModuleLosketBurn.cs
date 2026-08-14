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

		// --- Repere motif persistant ---
		// Position et orientation de la piece dans le repere du vaisseau,
		// capturees UNE FOIS au premier vol puis figees : le motif de bruit y
		// est echantillonne, donc continu entre pieces voisines (meme repere
		// capture) et definitivement colle a la piece. Un docking ulterieur ne
		// change rien : chaque vaisseau garde le repere qu'il a capture, la
		// seule couture est au port d'amarrage — les deux ont brule separement.

		[KSPField(isPersistant = true)] public bool patternFrameSet;
		[KSPField(isPersistant = true)] public Vector3 patternFramePos = Vector3.zero;
		[KSPField(isPersistant = true)] public Quaternion patternFrameRot = Quaternion.identity;

		// Fenetre du gradient d'etalement, en espace motif, a l'echelle du
		// vaisseau ENTIER : sans elle, chaque maillage refait son degrade de son
		// propre bord au vent a son propre bord oppose, et les joints font des
		// marches d'escalier. Recalculee pendant que la piece accumule (le depot
		// est en cours, son evolution est physique), figee des que la brulure
		// s'arrete : ni scintillement, ni saut au staging ou au docking.
		// Negatif = jamais calculee (repli : fenetre du maillage).

		[KSPField(isPersistant = true)] public float patternWindowMin;
		[KSPField(isPersistant = true)] public float patternWindowRange = -1f;

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

		// Voisins directs (parent + enfants) porteurs du module, pour lisser la
		// teinte du revenu. Rafraichi periodiquement : l'arbre de pieces change
		// au staging et au docking.
		private readonly System.Collections.Generic.List<ModuleLosketBurn> neighbours =
			new System.Collections.Generic.List<ModuleLosketBurn>();
		private int neighboursFrame = -1000;

		/// <summary>Matrice piece -> repere motif, pour ce module et la poussiere.</summary>
		public Matrix4x4 PatternMatrix
		{
			get
			{
				return patternFrameSet
					? Matrix4x4.TRS(patternFramePos, patternFrameRot, Vector3.one)
					: Matrix4x4.identity;
			}
		}

		/// <summary>
		/// Capture le repere du vaisseau si ce n'est pas deja fait. Appelee
		/// avant tout rendu en vol ; sans effet une fois la capture posee.
		/// </summary>
		public void EnsurePatternFrame()
		{
			if (patternFrameSet || vessel == null || vessel.rootPart == null) {
				return;
			}
			var root = vessel.rootPart.transform;
			patternFramePos = root.InverseTransformPoint(part.transform.position);
			patternFrameRot = Quaternion.Inverse(root.rotation) * part.transform.rotation;
			patternFrameSet = true;
		}

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

			// Le diagnostic de rendu suit l'option "Curseurs de developpement".
			var settings = LosketSettings.Instance;
			Events["DumpRenderState"].guiActive = settings != null && settings.interfaceDev;

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

		/// <summary>
		/// Diagnostic de rendu par piece (option "Curseurs de developpement").
		/// Nomme, pour chaque overlay, le renderer d'origine et son shader :
		/// c'est ce qui permet d'identifier a distance une piece dont l'overlay
		/// est eteint par le masque alpha ou saute par le filtre d'effets
		/// lumineux.
		/// </summary>
		[KSPEvent(guiActive = true, guiActiveEditor = false, guiName = "Journaliser (Losket)",
			groupName = Group, groupDisplayName = GroupTitle)]
		public void DumpRenderState()
		{
			var id = part.partInfo.name + "#" + GetInstanceID();
			LosketBootstrap.Log("=== DumpRenderState " + id + " ===");
			LosketBootstrap.Log("  affectedBy=" + affectedBy + " dose=" + dose.ToString("0") +
				" mag=" + BurnMag.ToString("0.00") +
				" peak=" + peakSkinTemp.ToString("0") + "K" +
				" dirStrength=" + (dose > 0f ? (dirAccum.magnitude / dose).ToString("0.00") : "n/a") +
				" fenetre=[" + patternWindowMin.ToString("0.0") + ", +" +
				patternWindowRange.ToString("0.0") + "]");
			if (rig == null) {
				LosketBootstrap.Log("  (pas de rig)");
				return;
			}
			for (var i = 0; i < rig.Count; i++) {
				var r = rig.Overlays[i];
				if (r == null) {
					LosketBootstrap.Log("  overlay[" + i + "] DETRUIT");
					continue;
				}
				var m = rig.Materials[i];
				var srcName = r.transform.parent != null ? r.transform.parent.name : "?";
				var srcRenderer = r.transform.parent != null
					? r.transform.parent.GetComponent<MeshRenderer>() : null;
				var srcShader = srcRenderer != null && srcRenderer.sharedMaterial != null &&
				                srcRenderer.sharedMaterial.shader != null
					? srcRenderer.sharedMaterial.shader.name : "?";
				LosketBootstrap.Log("  overlay[" + i + "] source='" + srcName +
					"' shaderBase='" + srcShader +
					"' useBaseAlpha=" + m.GetFloat("_UseBaseAlpha").ToString("0") +
					" visible=" + r.isVisible +
					" _BurnMag=" + m.GetFloat("_BurnMag").ToString("0.00"));
			}
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

			if (++windowTick >= 30) {
				windowTick = 0;
				RecomputePatternWindow();
			}
		}

		private int windowTick;

		/// <summary>
		/// Projette toutes les pieces du vaisseau sur l'axe du flux, dans
		/// l'espace motif de CETTE piece, et memorise les bornes. Appelee
		/// uniquement pendant l'accumulation : la fenetre est figee ensuite.
		/// </summary>
		private void RecomputePatternWindow()
		{
			EnsurePatternFrame();
			if (!patternFrameSet || dirAccum.sqrMagnitude < 1e-6f) {
				return;
			}

			var toPattern = PatternMatrix * part.transform.worldToLocalMatrix;
			var dirPattern = PatternMatrix.MultiplyVector(dirAccum.normalized).normalized;

			var lo = float.MaxValue;
			var hi = float.MinValue;
			var parts = vessel.parts;
			for (var i = 0; i < parts.Count; i++) {
				var other = parts[i];
				if (other == null) {
					continue;
				}
				var d = Vector3.Dot(
					toPattern.MultiplyPoint3x4(other.transform.position), dirPattern);
				if (d < lo) {
					lo = d;
				}
				if (d > hi) {
					hi = d;
				}
			}
			if (lo > hi) {
				return;
			}

			const float margin = 1.5f;
			patternWindowMin = lo - margin;
			patternWindowRange = hi - lo + 2f * margin;
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

			EnsurePatternFrame();

			var p = StyleParams();
			p.PartToPattern = PatternMatrix;
			if (patternWindowRange > 0f) {
				p.UsePatternWindow = true;
				p.PatternWindowMin = patternWindowMin;
				p.PatternWindowRange = patternWindowRange;
			}
			p.WorldFlowDir = part.transform.TransformDirection(flowPart);
			p.BurnMag = mag;
			p.PeakTemp = temperIntensity *
				Mathf.Clamp01((SmoothedPeakTemp() - temperMin) / (temperMax - temperMin));
			p.Wrap = Mathf.Lerp(2f, 1.3f, dirStrength);
			p.DirPower = Mathf.Lerp(0.5f, 1.5f, dirStrength);

			rig.Apply(p);
		}

		/// <summary>
		/// Temperature de pointe lissee avec les pieces attachees (40 % soi,
		/// 60 % moyenne du voisinage, soi compris). La simulation thermique de
		/// KSP donne des pics differents a des pieces voisines : sans lissage,
		/// la teinte du revenu fait des bandes nettes aux joints alors que le
		/// motif, lui, est continu. Rendu uniquement — l'etat persiste reste le
		/// pic reel de la piece.
		/// </summary>
		private float SmoothedPeakTemp()
		{
			if (Time.frameCount - neighboursFrame >= 60) {
				neighboursFrame = Time.frameCount;
				neighbours.Clear();
				if (part.parent != null) {
					var m = part.parent.FindModuleImplementing<ModuleLosketBurn>();
					if (m != null) {
						neighbours.Add(m);
					}
				}
				for (var i = 0; i < part.children.Count; i++) {
					var m = part.children[i].FindModuleImplementing<ModuleLosketBurn>();
					if (m != null) {
						neighbours.Add(m);
					}
				}
			}

			var sum = peakSkinTemp;
			var n = 1;
			for (var i = 0; i < neighbours.Count; i++) {
				var m = neighbours[i];
				if (m != null) {
					sum += m.peakSkinTemp;
					n++;
				}
			}
			return 0.4f * peakSkinTemp + 0.6f * (sum / n);
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
