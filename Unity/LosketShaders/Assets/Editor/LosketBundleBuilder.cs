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

			// A CONFIRMER : voir la ligne "espace colorimetrique" dans KSP.log au demarrage.
			// Si KSP rapporte Gamma, repasser cette valeur a ColorSpace.Gamma.
			PlayerSettings.colorSpace = ColorSpace.Linear;

			EditorUserBuildSettings.SwitchActiveBuildTarget(
				BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);

			AssetDatabase.SaveAssets();
			Debug.Log("[Losket] projet configure : D3D11 + OpenGLCore, espace colorimetrique "
			          + PlayerSettings.colorSpace + ", cible StandaloneWindows64.");
		}

		[MenuItem("Losket/Compiler le bundle de shaders %#b", false, 2)]
		public static void BuildBundle()
		{
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
			var manifest = BuildPipeline.BuildAssetBundles(
				TempBuildDir,
				BuildAssetBundleOptions.None,
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
	}
}
