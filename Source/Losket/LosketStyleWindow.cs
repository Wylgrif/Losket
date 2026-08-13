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

		private static LosketStyleWindow instance;

		private ModuleLosketBurn target;
		private Rect windowRect = new Rect(300f, 150f, 340f, 0f);
		private bool inputLocked;

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
			m.pattern = GUILayout.Toolbar(m.pattern > 0.5f ? 1 : 0, new[] {
				Localizer.Format("#LOC_Losket_Win_Blotches"),
				Localizer.Format("#LOC_Losket_Win_Streaks"),
			});
			GUILayout.EndHorizontal();

			GUILayout.Space(8f);
			GUILayout.Label(Localizer.Format("#LOC_Losket_Win_Presets"));
			GUILayout.BeginHorizontal();
			if (GUILayout.Button(Localizer.Format("#LOC_Losket_Preset_Soot"))) {
				m.ApplyPreset(LosketKeys.PresetSoot);
			}
			if (GUILayout.Button(Localizer.Format("#LOC_Losket_Preset_Metal"))) {
				m.ApplyPreset(LosketKeys.PresetMetal);
			}
			if (GUILayout.Button(Localizer.Format("#LOC_Losket_Preset_Streaks"))) {
				m.ApplyPreset(LosketKeys.PresetStreaks);
			}
			GUILayout.EndHorizontal();

			GUILayout.Space(8f);
			if (GUILayout.Button(Localizer.Format("#LOC_Losket_Win_Close"))) {
				target = null;
				ReleaseLock();
			}

			// Tout reglage manuel fait passer le preregle a "Personnalise".
			if (GUI.changed) {
				m.MarkCustomised();
			}

			GUI.DragWindow();
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
