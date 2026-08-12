using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Losket.EditorTools
{
	/// <summary>
	/// Compile les shaders de Assets/Shaders en un AssetBundle et le depose
	/// directement dans GameData/Losket/Shaders/ du depot.
	///
	/// Rien d'autre n'est necessaire cote Unity : pas de PartTools, pas de scene,
	/// pas de build de player.
	/// </summary>
	public static class LosketBundleBuilder
	{
		private const string BundleName = "losket";
		private const string OutputFileName = "Losket.shaderbundle";
		private const string ShaderFolder = "Assets/Shaders";

		/// <summary>Chemin du dossier temporaire de build, relatif au projet Unity.</summary>
		private const string TempBuildDir = "Temp/LosketBundle";

		/// <summary>
		/// Racine du depot Losket. Application.dataPath vaut
		/// &lt;depot&gt;/Unity/LosketShaders/Assets, donc on remonte de trois crans.
		/// </summary>
		private static string RepoRoot
		{
			get { return Path.GetFullPath(Path.Combine(Application.dataPath, "../../..")); }
		}

		[MenuItem("Losket/Configurer le projet", false, 1)]
		public static void ConfigureProject()
		{
			// KSP tourne en Direct3D11 par defaut, mais accepte -force-glcore.
			// On compile les variantes pour les deux, sinon les shaders sont roses en OpenGL.
			PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
			PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] {
				GraphicsDeviceType.Direct3D11,
				GraphicsDeviceType.OpenGLCore,
			});

			// Confirme le 2026-08-12 par la ligne de demarrage de LosketBootstrap :
			// KSP 1.12.5 tourne en espace colorimetrique Gamma.
			PlayerSettings.colorSpace = ColorSpace.Gamma;

			EditorUserBuildSettings.SwitchActiveBuildTarget(
				BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);

			AssetDatabase.SaveAssets();
			Debug.Log("[Losket] projet configure : D3D11 + OpenGLCore, espace colorimetrique "
			          + PlayerSettings.colorSpace + ", cible StandaloneWindows64.");
		}

		/// <summary>
		/// Verifie que le projet declare les modules indispensables a la production
		/// d'AssetBundles lisibles par un player.
		///
		/// Un manifeste allege produit un bundle d'apparence parfaitement normale
		/// (en-tete UnityFS valide, bonne version de serialisation, bonne cible) que
		/// le runtime refuse ensuite avec un message trompeur sur la version d'Unity.
		/// Le cas s'est produit ici : un manifeste ecrit a la main omettait
		/// com.unity.modules.assetbundle, et le diagnostic a coute plusieurs heures.
		/// </summary>
		private static bool ManifestLooksSane()
		{
			var manifestPath = Path.GetFullPath(Path.Combine(Application.dataPath,
				Path.Combine("..", Path.Combine("Packages", "manifest.json"))));

			if (!File.Exists(manifestPath)) {
				Debug.LogError("[Losket] Packages/manifest.json introuvable : " + manifestPath);
				return false;
			}

			var text = File.ReadAllText(manifestPath);
			var missing = new[] { "com.unity.modules.assetbundle" }
				.Where(m => !text.Contains(m)).ToArray();

			if (missing.Length > 0) {
				Debug.LogError("[Losket] Packages/manifest.json n'declare pas " +
				               string.Join(", ", missing) +
				               ". Le bundle serait produit sans erreur mais refuse au chargement. " +
				               "Utilise le manifeste par defaut d'Unity 2019.4 (38 modules).");
				return false;
			}

			return true;
		}

		[MenuItem("Losket/Compiler le bundle de shaders %#b", false, 2)]
		public static void BuildBundle()
		{
			if (!ManifestLooksSane()) {
				return;
			}

			if (!Directory.Exists(ShaderFolder)) {
				Debug.LogError("[Losket] dossier introuvable : " + ShaderFolder);
				return;
			}

			var shaderGuids = AssetDatabase.FindAssets("t:Shader", new[] { ShaderFolder });
			if (shaderGuids.Length == 0) {
				Debug.LogError("[Losket] aucun shader trouve dans " + ShaderFolder);
				return;
			}

			// 1) Verifier que tout compile, et rattacher chaque shader au bundle.
			var hasError = false;
			foreach (var guid in shaderGuids) {
				var path = AssetDatabase.GUIDToAssetPath(guid);
				var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);

				if (shader == null || ShaderUtil.ShaderHasError(shader)) {
					Debug.LogError("[Losket] erreur de compilation dans " + path);
					hasError = true;
					continue;
				}

				var importer = AssetImporter.GetAtPath(path);
				if (importer.assetBundleName != BundleName) {
					importer.assetBundleName = BundleName;
					importer.SaveAndReimport();
				}
			}

			if (hasError) {
				Debug.LogError("[Losket] build annule : corrige les erreurs de shader d'abord.");
				return;
			}

			// 2) Compiler le bundle.
			Directory.CreateDirectory(TempBuildDir);
			// LZ4 (ChunkBasedCompression) et non LZMA : c'est le format attendu pour un
			// chargement par AssetBundle.LoadFromFile au runtime.
			// ForceRebuild : sans lui, Unity reutilise le cache de Library/, ce qui peut
			// melanger des donnees produites sous d'anciens reglages (compression,
			// espace colorimetrique) avec le bundle courant.
			var manifest = BuildPipeline.BuildAssetBundles(
				TempBuildDir,
				BuildAssetBundleOptions.ChunkBasedCompression |
				BuildAssetBundleOptions.ForceRebuildAssetBundle,
				BuildTarget.StandaloneWindows64);

			if (manifest == null) {
				Debug.LogError("[Losket] BuildPipeline.BuildAssetBundles a echoue.");
				return;
			}

			var built = Path.Combine(TempBuildDir, BundleName);
			if (!File.Exists(built)) {
				Debug.LogError("[Losket] bundle attendu introuvable : " + built);
				return;
			}

			// 3) Copier dans le GameData du depot, sous notre propre extension.
			//    On evite volontairement .shab : Shabby capterait le fichier et Unity
			//    refuserait qu'on le charge une seconde fois depuis notre plugin.
			var outDir = Path.Combine(RepoRoot, Path.Combine("GameData", Path.Combine("Losket", "Shaders")));
			Directory.CreateDirectory(outDir);
			var outPath = Path.Combine(outDir, OutputFileName);
			File.Copy(built, outPath, true);

			var names = shaderGuids
				.Select(AssetDatabase.GUIDToAssetPath)
				.Select(Path.GetFileNameWithoutExtension)
				.ToArray();

			Debug.Log(string.Format(
				"[Losket] bundle ecrit : {0} ({1:N0} octets, {2} shader(s) : {3})",
				outPath, new FileInfo(outPath).Length, names.Length, string.Join(", ", names)));
		}

		/// <summary>
		/// Recharge le bundle produit avec la meme API que le plugin en jeu
		/// (AssetBundle.LoadFromFile) pour verifier qu'il est lisible au runtime,
		/// sans avoir a demarrer KSP.
		/// </summary>
		[MenuItem("Losket/Tester le chargement du bundle", false, 3)]
		public static void TestLoadBundle()
		{
			var path = Path.Combine(RepoRoot,
				Path.Combine("GameData", Path.Combine("Losket", Path.Combine("Shaders", OutputFileName))));

			if (!File.Exists(path)) {
				Debug.LogError("[LosketTest] bundle absent : " + path);
				return;
			}

			var bundle = AssetBundle.LoadFromFile(path);
			if (bundle == null) {
				Debug.LogError("[LosketTest] ECHEC : LoadFromFile a renvoye null pour " + path);
				return;
			}

			var loaded = bundle.LoadAllAssets<Shader>();
			Debug.Log("[LosketTest] OK : " + loaded.Length + " shader(s) — "
			          + string.Join(", ", loaded.Select(s => s.name + (s.isSupported ? "" : " (NON SUPPORTE)")).ToArray()));
			bundle.Unload(true);
		}
	}
}
