using System.Collections.Generic;
using KSP.Localization;
using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Style de brulure pose d'un coup sur toutes les pieces du vaisseau en
	/// construction. Sans cela un vaisseau de deux cents pieces demande deux
	/// cents menus de piece pour changer de preregle.
	///
	/// Rien de neuf n'est stocke : chaque piece garde son propre style
	/// persistant, exactement comme si le joueur l'avait regle a la main. Une
	/// piece posee ensuite arrive donc avec le preregle par defaut.
	/// </summary>
	public static class LosketVesselStyle
	{
		/// <summary>Modules de brulure du vaisseau de l'editeur. Les pieces
		/// detachees (tenues a la souris, posees a cote) n'en font pas partie :
		/// c'est le vaisseau que le jeu enregistrerait.</summary>
		public static void Collect(List<ModuleLosketBurn> into)
		{
			into.Clear();
			if (!HighLogic.LoadedSceneIsEditor || EditorLogic.fetch == null ||
			    EditorLogic.fetch.ship == null) {
				return;
			}
			var parts = EditorLogic.fetch.ship.parts;
			for (var i = 0; i < parts.Count; i++) {
				if (parts[i] == null) {
					continue;
				}
				var m = parts[i].FindModuleImplementing<ModuleLosketBurn>();
				if (m != null) {
					into.Add(m);
				}
			}
		}

		/// <summary>Applique un preregle (livre ou du joueur) a tout le
		/// vaisseau. Le type de bouclier ne sert qu'au preregle Tuiles.
		/// Renvoie le nombre de pieces touchees.</summary>
		public static int ApplyPreset(string key, string shield)
		{
			var modules = new List<ModuleLosketBurn>();
			Collect(modules);
			for (var i = 0; i < modules.Count; i++) {
				if (key == LosketKeys.PresetTiles) {
					modules[i].tileShield = shield;
				}
				modules[i].ApplyPreset(key);
			}
			Finish(modules);
			return modules.Count;
		}

		/// <summary>Change le type de bouclier des pieces deja au preregle
		/// Tuiles, et d'elles seules : meme effet que la ligne "Bouclier" du
		/// menu de piece.</summary>
		public static int ApplyShield(string shield)
		{
			var modules = new List<ModuleLosketBurn>();
			Collect(modules);
			var touched = new List<ModuleLosketBurn>();
			for (var i = 0; i < modules.Count; i++) {
				if (modules[i].presetName != LosketKeys.PresetTiles) {
					continue;
				}
				modules[i].tileShield = shield;
				modules[i].ApplyPreset(LosketKeys.PresetTiles);
				touched.Add(modules[i]);
			}
			Finish(touched);
			return touched.Count;
		}

		/// <summary>Recopie le style d'une piece (reglages Personnalise
		/// compris) sur toutes les autres. Renvoie le nombre de pieces
		/// touchees, la source non comptee.</summary>
		public static int CopyFrom(ModuleLosketBurn source)
		{
			var modules = new List<ModuleLosketBurn>();
			Collect(modules);
			modules.Remove(source);
			var style = LosketBurnStyle.From(source, "");
			for (var i = 0; i < modules.Count; i++) {
				style.ApplyTo(modules[i]);
				modules[i].presetName = source.presetName;
				modules[i].tileShield = source.tileShield;
			}
			Finish(modules);
			return modules.Count;
		}

		public static void SetPreview(bool on)
		{
			var modules = new List<ModuleLosketBurn>();
			Collect(modules);
			for (var i = 0; i < modules.Count; i++) {
				modules[i].SetEditorPreview(on);
			}
		}

		/// <summary>Les menus de piece ouverts se redessinent, et l'editeur
		/// apprend que le vaisseau a change : c'est ce que fait KSP apres un
		/// reglage dans un menu de piece (UIPartActionFieldItem.SetFieldValue).</summary>
		private static void Finish(List<ModuleLosketBurn> modules)
		{
			if (modules.Count == 0) {
				return;
			}
			for (var i = 0; i < modules.Count; i++) {
				MonoUtilities.RefreshPartContextWindow(modules[i].part);
			}
			if (EditorLogic.fetch != null && EditorLogic.fetch.ship != null) {
				GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
			}
		}
	}

	/// <summary>
	/// Fenetre "Vaisseau entier" de l'editeur (VAB et hangar), ouverte depuis
	/// la fenetre du bouton de la barre d'applications : un clic sur un
	/// preregle le pose sur toutes les pieces.
	/// </summary>
	public class LosketVesselWindow : MonoBehaviour
	{
		private const string LockId = "LosketVesselWindow";

		private static LosketVesselWindow instance;

		private bool visible;
		private Rect windowRect = new Rect(560f, 120f, 340f, 0f);
		private bool inputLocked;
		private Vector2 presetScroll;
		private string status = "";
		private string shownStatus = "";
		private float statusUntil;

		// Etat du vaisseau, releve quelques fois par seconde et non a chaque
		// passe d'OnGUI : nombre de pieces, preregle commun (null = plusieurs),
		// presence de pieces Tuiles et leur type de bouclier commun, apercu
		// allume quelque part.
		private readonly List<ModuleLosketBurn> modules = new List<ModuleLosketBurn>();
		private int partCount;
		private string commonPreset;
		private string shield = LosketKeys.ShieldFlight15;
		private bool anyPreview;
		private bool anyTiles;
		private bool showShield;
		private float nextSurvey;

		public static void Toggle()
		{
			if (instance == null) {
				// Pas de DontDestroyOnLoad : la fenetre n'a de sens que dans
				// l'editeur et disparait avec sa scene.
				var go = new GameObject("LosketVesselWindow");
				instance = go.AddComponent<LosketVesselWindow>();
			}
			instance.visible = !instance.visible;
			if (instance.visible) {
				instance.Survey();
			} else {
				instance.ReleaseLock();
			}
		}

		private void Update()
		{
			if (visible && Time.realtimeSinceStartup >= nextSurvey) {
				Survey();
			}
		}

		private void Survey()
		{
			nextSurvey = Time.realtimeSinceStartup + 0.25f;
			LosketVesselStyle.Collect(modules);
			partCount = modules.Count;
			commonPreset = partCount > 0 ? modules[0].presetName : null;
			anyPreview = false;
			string tilesShield = null;
			var tilesMixed = false;
			for (var i = 0; i < modules.Count; i++) {
				var m = modules[i];
				if (m.presetName != commonPreset) {
					commonPreset = null;
				}
				anyPreview |= m.EditorPreview;
				if (m.presetName == LosketKeys.PresetTiles) {
					if (tilesShield == null) {
						tilesShield = m.tileShield;
					} else if (tilesShield != m.tileShield) {
						tilesMixed = true;
					}
				}
			}
			// Le selecteur suit le vaisseau tant que ses pieces Tuiles sont
			// d'accord ; sinon il garde le dernier choix du joueur.
			anyTiles = tilesShield != null;
			if (anyTiles && !tilesMixed) {
				shield = tilesShield;
			}
			modules.Clear();
		}

		private void OnGUI()
		{
			if (!visible) {
				return;
			}
			if (!HighLogic.LoadedSceneIsEditor) {
				visible = false;
				ReleaseLock();
				return;
			}

			GUI.skin = HighLogic.Skin;
			windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow,
				Localizer.Format("#LOC_Losket_Vessel_Title"));

			// Bloque les clics de l'editeur sous la fenetre.
			var hover = windowRect.Contains(new Vector2(Input.mousePosition.x,
				Screen.height - Input.mousePosition.y));
			if (hover && !inputLocked) {
				InputLockManager.SetControlLock(ControlTypes.EDITOR_UI, LockId);
				inputLocked = true;
			} else if (!hover && inputLocked) {
				ReleaseLock();
			}
		}

		private void DrawWindow(int id)
		{
			GUILayout.Label(Localizer.Format("#LOC_Losket_Vessel_Intro"));
			if (partCount == 0) {
				GUILayout.Label(Localizer.Format("#LOC_Losket_Vessel_Empty"));
			} else {
				GUILayout.Label(Localizer.Format("#LOC_Losket_Vessel_Current",
					commonPreset != null
						? LosketKeys.PresetLabel(commonPreset)
						: Localizer.Format("#LOC_Losket_Vessel_Mixed"),
					partCount));
			}

			GUILayout.Space(6f);
			GUILayout.Label(Localizer.Format("#LOC_Losket_Win_Presets"));
			var builtIn = LosketKeys.BuiltInPresets;
			for (var row = 0; row < builtIn.Length; row += 3) {
				GUILayout.BeginHorizontal();
				for (var k = row; k < builtIn.Length && k < row + 3; k++) {
					PresetButton(builtIn[k]);
				}
				GUILayout.EndHorizontal();
			}

			// Type de bouclier du preregle Tuiles : comme dans le menu de
			// piece, la ligne n'existe que si des pieces sont en Tuiles, et la
			// changer les reapplique sur-le-champ. Visibilite figee a la passe
			// Layout : un clic sur Tuiles la fait apparaitre en cours de passe.
			if (Event.current.type == EventType.Layout) {
				showShield = anyTiles;
			}
			if (showShield) {
				GUILayout.BeginHorizontal();
				GUILayout.Label(Localizer.Format("#LOC_Losket_TileShield"),
					GUILayout.Width(110f));
				var options = LosketKeys.ShieldOptions;
				var current = Mathf.Max(0, System.Array.IndexOf(options, shield));
				var chosen = GUILayout.Toolbar(current, LosketKeys.ShieldDisplay());
				GUILayout.EndHorizontal();
				if (chosen != current) {
					shield = options[chosen];
					var n = LosketVesselStyle.ApplyShield(shield);
					if (n > 0) {
						Say(Localizer.Format("#LOC_Losket_Vessel_Applied",
							LosketKeys.PresetLabel(LosketKeys.PresetTiles), n));
					}
					Survey();
				}
			}

			var all = LosketUserPresets.All;
			if (all.Count > 0) {
				GUILayout.Space(6f);
				GUILayout.Label(Localizer.Format("#LOC_Losket_Win_UserPresets"));
				var rows = Mathf.Min(all.Count, 5);
				presetScroll = GUILayout.BeginScrollView(presetScroll,
					GUILayout.Height(rows * 30f + 8f));
				for (var i = 0; i < all.Count; i++) {
					PresetButton(LosketKeys.UserPresetKey(all[i].name));
				}
				GUILayout.EndScrollView();
			}

			GUILayout.Space(6f);
			if (GUILayout.Button(Localizer.Format(anyPreview
				    ? "#LOC_Losket_PreviewHide" : "#LOC_Losket_PreviewShow"))) {
				LosketVesselStyle.SetPreview(!anyPreview);
				Survey();
			}

			// Le message affiche est fige a la passe Layout : un clic le change
			// en cours de passe, et une ligne apparue entre Layout et la suite
			// ferait perdre a IMGUI le compte de ses controles.
			if (Event.current.type == EventType.Layout) {
				shownStatus = Time.realtimeSinceStartup < statusUntil ? status : "";
			}
			if (!string.IsNullOrEmpty(shownStatus)) {
				GUILayout.Label(shownStatus);
			}

			GUILayout.Space(8f);
			if (GUILayout.Button(Localizer.Format("#LOC_Losket_Win_Close"))) {
				visible = false;
				ReleaseLock();
			}

			GUI.DragWindow();
		}

		/// <summary>Bouton d'un preregle ; celui que tout le vaisseau porte
		/// deja est marque, comme dans la fenetre de reglages d'une piece.</summary>
		private void PresetButton(string key)
		{
			var label = LosketKeys.PresetLabel(key);
			if (GUILayout.Button(commonPreset == key ? "> " + label : label)) {
				var n = LosketVesselStyle.ApplyPreset(key, shield);
				Say(n > 0
					? Localizer.Format("#LOC_Losket_Vessel_Applied", label, n)
					: Localizer.Format("#LOC_Losket_Vessel_Empty"));
				Survey();
			}
		}

		private void Say(string message)
		{
			status = message;
			statusUntil = Time.realtimeSinceStartup + 4f;
		}

		private void ReleaseLock()
		{
			if (inputLocked) {
				InputLockManager.RemoveControlLock(LockId);
				inputLocked = false;
			}
		}

		private void OnDestroy()
		{
			ReleaseLock();
			if (instance == this) {
				instance = null;
			}
		}
	}
}
