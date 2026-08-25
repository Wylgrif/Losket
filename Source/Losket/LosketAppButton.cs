using KSP.UI.Screens;
using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Bouton Losket dans la barre d'applications stock (centre spatial,
	/// editeur, vol) : le point d'entree decouvrable vers les reglages du mod.
	/// Les options de difficulte restent le lieu de stockage, mais personne ne
	/// pense a aller les y chercher.
	/// </summary>
	[KSPAddon(KSPAddon.Startup.MainMenu, true)]
	public class LosketAppButton : MonoBehaviour
	{
		private static ApplicationLauncherButton button;

		private void Awake()
		{
			DontDestroyOnLoad(gameObject);
			GameEvents.onGUIApplicationLauncherReady.Add(OnLauncherReady);
			GameEvents.onGUIApplicationLauncherDestroyed.Add(OnLauncherDestroyed);
		}

		private void OnLauncherReady()
		{
			if (button != null || ApplicationLauncher.Instance == null) {
				return;
			}

			var icon = GameDatabase.Instance.GetTexture("Losket/Textures/toolbar", false);
			if (icon == null) {
				LosketBootstrap.LogWarning(
					"icone de barre d'applications introuvable (Losket/Textures/toolbar) " +
					"- genere-la avec Tools/make_toolbar_icon.py");
				return;
			}

			button = ApplicationLauncher.Instance.AddModApplication(
				LosketSettingsWindow.Toggle, LosketSettingsWindow.Toggle,
				null, null, null, null,
				ApplicationLauncher.AppScenes.SPACECENTER |
				ApplicationLauncher.AppScenes.FLIGHT |
				ApplicationLauncher.AppScenes.VAB |
				ApplicationLauncher.AppScenes.SPH,
				icon);
		}

		private void OnLauncherDestroyed()
		{
			if (button != null) {
				ApplicationLauncher.Instance.RemoveModApplication(button);
				button = null;
			}
		}
	}
}
