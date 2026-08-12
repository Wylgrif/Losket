using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Point d'entree unique du mod.
	///
	/// Charge le bundle de shaders une seule fois au demarrage du jeu et le garde
	/// en memoire pour toute la session. On charge le bundle nous-memes plutot que
	/// de passer par Shabby : Shabby capte l'extension .shab, donc si les deux
	/// chargeaient le meme fichier Unity refuserait le second chargement. Notre
	/// extension .shaderbundle est ignoree par le GameDatabase de KSP.
	/// </summary>
	[KSPAddon(KSPAddon.Startup.Instantly, true)]
	public class LosketBootstrap : MonoBehaviour
	{
		public const string ModName = "Losket";

		private const string BundleFileName = "Losket.shaderbundle";

		private static readonly Dictionary<string, Shader> shaders =
			new Dictionary<string, Shader>();

		/// <summary>Vrai si le bundle a ete charge et contient au moins un shader.</summary>
		public static bool ShadersLoaded { get; private set; }

		/// <summary>Recupere un shader du bundle par son nom, ou null s'il est absent.</summary>
		public static Shader GetShader(string name)
		{
			Shader shader;
			return shaders.TryGetValue(name, out shader) ? shader : null;
		}

		private void Awake()
		{
			DontDestroyOnLoad(gameObject);

			var version = Assembly.GetExecutingAssembly().GetName().Version;
			Log(string.Format(
				"v{0} — demarrage. KSP {1}.{2}.{3}, Unity {4}, rendu {5}, " +
				"espace colorimetrique {6}, chemin de rendu {7}",
				version,
				Versioning.version_major, Versioning.version_minor, Versioning.Revision,
				Application.unityVersion,
				SystemInfo.graphicsDeviceType,
				QualitySettings.activeColorSpace,
				Camera.main != null ? Camera.main.actualRenderingPath.ToString() : "(pas de camera)"));

			LoadShaderBundle();
		}

		private void LoadShaderBundle()
		{
			var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
			var bundlePath = Path.GetFullPath(
				Path.Combine(pluginDir, Path.Combine("..", Path.Combine("Shaders", BundleFileName))));

			if (!File.Exists(bundlePath)) {
				LogWarning("bundle de shaders introuvable : " + bundlePath +
				           " — compile-le depuis Unity (menu Losket > Build Shader Bundle).");
				return;
			}

			AssetBundle bundle = null;
			try {
				bundle = AssetBundle.LoadFromFile(bundlePath);
			} catch (Exception e) {
				LogError("echec du chargement du bundle : " + e);
				return;
			}

			if (bundle == null) {
				LogError("AssetBundle.LoadFromFile a renvoye null pour " + bundlePath +
				         " — bundle compile pour la mauvaise plateforme ou une version d'Unity differente ?");
				return;
			}

			foreach (var shader in bundle.LoadAllAssets<Shader>()) {
				shaders[shader.name] = shader;
				if (shader.isSupported) {
					Log("shader charge : " + shader.name);
				} else {
					LogError("shader non supporte par ce GPU/API : " + shader.name);
				}
			}

			// Libere le fichier sur disque sans detruire les shaders deja charges.
			bundle.Unload(false);

			ShadersLoaded = shaders.Count > 0;
			Log(shaders.Count + " shader(s) charge(s) depuis " + BundleFileName);
		}

		internal static void Log(string message)
		{
			Debug.Log("[" + ModName + "] " + message);
		}

		internal static void LogWarning(string message)
		{
			Debug.LogWarning("[" + ModName + "] " + message);
		}

		internal static void LogError(string message)
		{
			Debug.LogError("[" + ModName + "] " + message);
		}
	}
}
