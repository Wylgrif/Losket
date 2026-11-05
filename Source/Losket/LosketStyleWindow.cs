using KSP.Localization;
using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Fenetre de reglages avances du style de brulure, sur le modele de TURD :
	/// le PAW ne porte que le minimum (preregle + bouton), les curseurs vivent
	/// ici. Edite directement les champs persistants du ModuleLosketBurn cible.
	/// </summary>
	public class LosketStyleWindow : MonoBehaviour
	{
		private const string LockId = "LosketStyleWindow";
		private const string KeyboardLockId = "LosketStyleWindowKeyboard";
		private const string NameFieldId = "LosketPresetName";

		private static LosketStyleWindow instance;

		private ModuleLosketBurn target;
		private Rect windowRect = new Rect(300f, 150f, 340f, 0f);
		private bool inputLocked;
		private bool keyboardLocked;

		// Section des preregles enregistres : nom en cours de saisie, liste
		// deroulante, et dernier message d'etat (enregistre, colle, erreur).
		private string presetNameInput = "";
		private Vector2 presetScroll;
		private string status = "";
		private float statusUntil;

		public static void Open(ModuleLosketBurn module)
		{
			if (instance == null) {
				var go = new GameObject("LosketStyleWindow");
				instance = go.AddComponent<LosketStyleWindow>();
			}
			instance.target = module;
		}

		public static void CloseIfTarget(ModuleLosketBurn module)
		{
			if (instance != null && instance.target == module) {
				instance.target = null;
			}
		}

		private void OnGUI()
		{
			if (target == null) {
				ReleaseLock();
				return;
			}

			GUI.skin = HighLogic.Skin;
			windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow,
				"Losket - " + target.part.partInfo.title);

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
			var m = target;

			GUILayout.Label(Localizer.Format("#LOC_Losket_Win_Deposit"));
			m.sootR = Slider(Localizer.Format("#LOC_Losket_Win_R"), m.sootR, 0f, 1f);
			m.sootG = Slider(Localizer.Format("#LOC_Losket_Win_G"), m.sootG, 0f, 1f);
			m.sootB = Slider(Localizer.Format("#LOC_Losket_Win_B"), m.sootB, 0f, 1f);

			var prev = GUI.color;
			GUI.color = new Color(m.sootR, m.sootG, m.sootB);
			GUILayout.Box("", GUILayout.Height(14f), GUILayout.ExpandWidth(true));
			GUI.color = prev;

			GUILayout.Space(6f);
			m.temperIntensity = Slider(Localizer.Format("#LOC_Losket_Win_Iridescence"),
				m.temperIntensity, 0f, 1f);
			m.bleach = Slider(Localizer.Format("#LOC_Losket_Win_Bleach"), m.bleach, 0f, 1f);
			m.sharpness = Slider(Localizer.Format("#LOC_Losket_Win_Sharpness"),
				m.sharpness, 0.5f, 8f);
			m.noiseScale = Slider(Localizer.Format("#LOC_Losket_Win_NoiseScale"),
				m.noiseScale, 0.5f, 16f);
			m.streak = Slider(Localizer.Format("#LOC_Losket_Win_StreakLength"),
				m.streak, 1f, 16f);

			GUILayout.Space(6f);
			GUILayout.BeginHorizontal();
			GUILayout.Label(Localizer.Format("#LOC_Losket_Win_Pattern"), GUILayout.Width(110f));
			m.pattern = GUILayout.Toolbar(Mathf.Clamp(Mathf.RoundToInt(m.pattern), 0, 2), new[] {
				Localizer.Format("#LOC_Losket_Win_Blotches"),
				Localizer.Format("#LOC_Losket_Win_Streaks"),
				Localizer.Format("#LOC_Losket_Win_Tiles"),
			});
			GUILayout.EndHorizontal();

			// Motif Tuiles : ses propres reglages, visibles seulement quand
			// il est choisi. Voir docs/traces-starship.md.
			if (m.pattern > 1.5f) {
				GUILayout.Space(4f);
				m.tileSize = Slider(Localizer.Format("#LOC_Losket_Win_TileSize"),
					m.tileSize, 0.05f, 1f);
				m.tileLeak = Slider(Localizer.Format("#LOC_Losket_Win_TileLeak"),
					m.tileLeak, 0f, 0.3f);
				m.tileBroken = Slider(Localizer.Format("#LOC_Losket_Win_TileBroken"),
					m.tileBroken, 0f, 0.05f);
				m.tileLength = Slider(Localizer.Format("#LOC_Losket_Win_TileLength"),
					m.tileLength, 0.2f, 5f);
				m.tileFan = Slider(Localizer.Format("#LOC_Losket_Win_TileFan"),
					m.tileFan, 0f, 20f);
				m.tileEdgeWear = Slider(Localizer.Format("#LOC_Losket_Win_TileEdge"),
					m.tileEdgeWear, 0f, 2f);
			}

			// Reflets sous le depot : eteints (suie, poussiere) ou conserves
			// (metal brule). Voir _Matte dans le shader.
			GUILayout.Space(4f);
			GUILayout.BeginHorizontal();
			GUILayout.Label(Localizer.Format("#LOC_Losket_Win_Reflections"), GUILayout.Width(110f));
			m.matte = GUILayout.Toolbar(m.matte ? 0 : 1, new[] {
				Localizer.Format("#LOC_Losket_Win_ReflDimmed"),
				Localizer.Format("#LOC_Losket_Win_ReflKept"),
			}) == 0;
			GUILayout.EndHorizontal();

			// Tout reglage manuel fait passer le preregle a "Personnalise".
			// Teste ICI, avant la section des preregles : le champ de nom
			// leve aussi GUI.changed, et taper un nom n'est pas un reglage.
			if (GUI.changed) {
				m.MarkCustomised();
				GUI.changed = false;
			}

			GUILayout.Space(8f);
			GUILayout.Label(Localizer.Format("#LOC_Losket_Win_Presets"));
			// Deux rangees de trois : six boutons sur une ligne de 340 px
			// tronquaient les libelles.
			var builtIn = LosketKeys.BuiltInPresets;
			for (var row = 0; row < builtIn.Length; row += 3) {
				GUILayout.BeginHorizontal();
				for (var k = row; k < builtIn.Length && k < row + 3; k++) {
					if (GUILayout.Button(LosketKeys.PresetLabel(builtIn[k]))) {
						m.ApplyPreset(builtIn[k]);
					}
				}
				GUILayout.EndHorizontal();
			}

			DrawUserPresets(m);

			GUILayout.Space(8f);
			if (GUILayout.Button(Localizer.Format("#LOC_Losket_Win_Close"))) {
				target = null;
				ReleaseLock();
			}

			GUI.DragWindow();
		}

		/// <summary>
		/// Preregles du joueur : un nom et "Enregistrer" pour garder les
		/// reglages courants, la liste de ceux enregistres (appliquer, copier,
		/// supprimer), et l'import depuis le presse-papiers ou le dossier.
		/// </summary>
		private void DrawUserPresets(ModuleLosketBurn m)
		{
			GUILayout.Space(6f);
			GUILayout.Label(Localizer.Format("#LOC_Losket_Win_UserPresets"));

			GUILayout.BeginHorizontal();
			GUI.SetNextControlName(NameFieldId);
			presetNameInput = GUILayout.TextField(presetNameInput, 40);
			if (GUILayout.Button(Localizer.Format("#LOC_Losket_Win_Save"), GUILayout.Width(90f))) {
				SaveCurrent(m);
			}
			GUILayout.EndHorizontal();
			// Les touches de l'editeur (raccourcis d'outils, de symetrie...)
			// restent muettes tant que le champ de nom a le focus.
			LockKeyboard(GUI.GetNameOfFocusedControl() == NameFieldId);

			var all = LosketUserPresets.All;
			if (all.Count > 0) {
				var rows = Mathf.Min(all.Count, 5);
				presetScroll = GUILayout.BeginScrollView(presetScroll,
					GUILayout.Height(rows * 30f + 8f));
				string toDelete = null;
				foreach (var style in all) {
					GUILayout.BeginHorizontal();
					var key = LosketKeys.UserPresetKey(style.name);
					var label = m.presetName == key ? "> " + style.name : style.name;
					if (GUILayout.Button(label)) {
						m.ApplyPreset(key);
						presetNameInput = style.name;
					}
					if (GUILayout.Button(Localizer.Format("#LOC_Losket_Win_Copy"),
						    GUILayout.Width(70f))) {
						LosketUserPresets.CopyToClipboard(style);
						Say(Localizer.Format("#LOC_Losket_Win_Copied", style.name));
					}
					if (GUILayout.Button("X", GUILayout.Width(26f))) {
						toDelete = style.name;
					}
					GUILayout.EndHorizontal();
				}
				GUILayout.EndScrollView();
				if (toDelete != null) {
					LosketUserPresets.Delete(toDelete);
					Say(Localizer.Format("#LOC_Losket_Win_Deleted", toDelete));
				}
			}

			GUILayout.BeginHorizontal();
			if (GUILayout.Button(Localizer.Format("#LOC_Losket_Win_Paste"))) {
				PasteFromClipboard(m);
			}
			if (GUILayout.Button(Localizer.Format("#LOC_Losket_Win_Reload"))) {
				LosketUserPresets.Load();
				Say(Localizer.Format("#LOC_Losket_Win_Reloaded", LosketUserPresets.All.Count));
			}
			if (GUILayout.Button(Localizer.Format("#LOC_Losket_Win_Folder"))) {
				LosketUserPresets.OpenFolder();
			}
			GUILayout.EndHorizontal();

			if (Time.realtimeSinceStartup < statusUntil && !string.IsNullOrEmpty(status)) {
				GUILayout.Label(status);
			}
		}

		/// <summary>Enregistre les reglages courants de la piece sous le nom
		/// saisi ; la piece prend alors ce preregle comme nom.</summary>
		private void SaveCurrent(ModuleLosketBurn m)
		{
			var name = LosketUserPresets.CleanName(presetNameInput);
			if (string.IsNullOrEmpty(name)) {
				Say(Localizer.Format("#LOC_Losket_Win_NeedName"));
				return;
			}
			var style = LosketBurnStyle.From(m, name);
			if (!LosketUserPresets.Save(style)) {
				Say(Localizer.Format("#LOC_Losket_Win_SaveFailed"));
				return;
			}
			presetNameInput = style.name;
			m.presetName = LosketKeys.UserPresetKey(style.name);
			Say(Localizer.Format("#LOC_Losket_Win_Saved", style.name));
		}

		/// <summary>Importe un preregle depuis le presse-papiers : enregistre
		/// sous son nom (remplace un homonyme) et applique a la piece.</summary>
		private void PasteFromClipboard(ModuleLosketBurn m)
		{
			var style = LosketUserPresets.FromClipboard();
			if (style == null) {
				Say(Localizer.Format("#LOC_Losket_Win_PasteFailed"));
				return;
			}
			if (!LosketUserPresets.Save(style)) {
				Say(Localizer.Format("#LOC_Losket_Win_SaveFailed"));
				return;
			}
			style.ApplyTo(m);
			m.presetName = LosketKeys.UserPresetKey(style.name);
			presetNameInput = style.name;
			Say(Localizer.Format("#LOC_Losket_Win_Pasted", style.name));
		}

		private void Say(string message)
		{
			status = message;
			statusUntil = Time.realtimeSinceStartup + 4f;
		}

		private void LockKeyboard(bool wanted)
		{
			if (wanted && !keyboardLocked) {
				InputLockManager.SetControlLock(ControlTypes.KEYBOARDINPUT, KeyboardLockId);
				keyboardLocked = true;
			} else if (!wanted && keyboardLocked) {
				InputLockManager.RemoveControlLock(KeyboardLockId);
				keyboardLocked = false;
			}
		}

		private static float Slider(string label, float value, float min, float max)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label(label, GUILayout.Width(110f));
			var v = GUILayout.HorizontalSlider(value, min, max);
			GUILayout.Label(v.ToString("0.00"), GUILayout.Width(40f));
			GUILayout.EndHorizontal();
			return v;
		}

		private void ReleaseLock()
		{
			if (inputLocked) {
				InputLockManager.RemoveControlLock(LockId);
				inputLocked = false;
			}
			LockKeyboard(false);
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
