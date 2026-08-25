using KSP.Localization;
using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Fenetre des reglages globaux, ouverte par le bouton de la barre
	/// d'applications. Edite directement les parametres de partie
	/// (LosketSettings) : memes valeurs que l'onglet Losket des options de
	/// difficulte, juste accessibles sans les chercher.
	/// </summary>
	public class LosketSettingsWindow : MonoBehaviour
	{
		private const string LockId = "LosketSettingsWindow";

		private static LosketSettingsWindow instance;

		private bool visible;
		private Rect windowRect = new Rect(200f, 120f, 340f, 0f);
		private bool inputLocked;

		public static void Toggle()
		{
			if (instance == null) {
				var go = new GameObject("LosketSettingsWindow");
				DontDestroyOnLoad(go);
				instance = go.AddComponent<LosketSettingsWindow>();
			}
			instance.visible = !instance.visible;
			if (!instance.visible) {
				instance.ReleaseLock();
			}
		}

		private void OnGUI()
		{
			if (!visible) {
				return;
			}

			GUI.skin = HighLogic.Skin;
			windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow,
				Localizer.Format("#LOC_Losket_Group"));

			// Bloque les clics de l'editeur sous la fenetre.
			if (HighLogic.LoadedSceneIsEditor) {
				var hover = windowRect.Contains(new Vector2(Input.mousePosition.x,
					Screen.height - Input.mousePosition.y));
				if (hover && !inputLocked) {
					InputLockManager.SetControlLock(ControlTypes.EDITOR_UI, LockId);
					inputLocked = true;
				} else if (!hover && inputLocked) {
					ReleaseLock();
				}
			}
		}

		private void DrawWindow(int id)
		{
			var settings = LosketSettings.Instance;
			if (settings == null) {
				GUILayout.Label(Localizer.Format("#LOC_Losket_Set_NoGame"));
			} else {
				settings.burnByDefault = GUILayout.Toggle(settings.burnByDefault,
					Localizer.Format("#LOC_Losket_Set_Burn"));
				settings.dustByDefault = GUILayout.Toggle(settings.dustByDefault,
					Localizer.Format("#LOC_Losket_Set_Dust"));
				GUILayout.Space(6f);
				settings.interfaceDev = GUILayout.Toggle(settings.interfaceDev,
					Localizer.Format("#LOC_Losket_Set_DevUI"));
				GUILayout.Space(6f);
				GUILayout.Label(Localizer.Format("#LOC_Losket_Set_Note"));
			}

			GUILayout.Space(8f);
			if (GUILayout.Button(Localizer.Format("#LOC_Losket_Win_Close"))) {
				visible = false;
				ReleaseLock();
			}

			GUI.DragWindow();
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
