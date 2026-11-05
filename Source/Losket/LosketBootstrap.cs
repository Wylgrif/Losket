using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
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
	///
	/// Le point d'entree est MainMenu et non Instantly. A Instantly le moteur
	/// rejette le bundle avec un message trompeur sur la version d'Unity ; le
	/// systeme d'assets n'est pas encore pret. Les bundles stock de KSP sont
	/// charges bien plus tard, et le meme fichier passe sans probleme a MainMenu.
	/// </summary>
	[KSPAddon(KSPAddon.Startup.MainMenu, true)]
	public class LosketBootstrap : MonoBehaviour
	{
		public const string ModName = "Losket";

		private const string BundleFileName = "Losket.shaderbundle";

		private static readonly Dictionary<string, Shader> shaders =
			new Dictionary<string, Shader>();

		/// <summary>Vrai si le bundle a ete charge et contient au moins un shader.</summary>
		public static bool ShadersLoaded { get; private set; }

		/// <summary>
		/// LUT de revenu (temperature -> teinte), generee par Tools/make_temper_lut.py.
		/// Stockee dans PluginData et chargee ici plutot que par le GameDatabase :
		/// KSP compresserait le degrade en DXT, ce qui y dessinerait des bandes.
		/// </summary>
		public static Texture2D TemperLut { get; private set; }

		private static readonly Dictionary<string, Color> dustColors =
			new Dictionary<string, Color>();

		private static readonly Color DefaultDustColor = new Color(0.5f, 0.45f, 0.4f);

		/// <summary>Pression (atm) sous laquelle les moteurs ne soulevent pas
		/// de poussiere. Voir Configs/losket-settings.cfg. 0 = partout.</summary>
		public static float DustMinPressureAtm { get; private set; }

		/// <summary>Couleur de la poussiere du corps, depuis les noeuds
		/// LOSKET_BODY_DUST (voir Configs/dust-colors.cfg). Le gris-brun par
		/// defaut si le corps n'a pas d'entree ou si le joueur a decoche la
		/// couleur par astre dans les reglages. Lue a chaque mise a jour de
		/// la source : le reglage agit sur les depots a venir, la teinte deja
		/// accumulee sur une piece reste celle de son depot.</summary>
		public static Color GetDustColor(CelestialBody body)
		{
			Color color;
			return body != null && LosketSettings.DustBodyColors() &&
			       dustColors.TryGetValue(body.bodyName, out color)
				? color
				: DefaultDustColor;
		}

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
				"v{0} - demarrage. KSP {1}.{2}.{3}, Unity {4}, rendu {5}, " +
				"espace colorimetrique {6}, chemin de rendu {7}",
				version,
				Versioning.version_major, Versioning.version_minor, Versioning.Revision,
				Application.unityVersion,
				SystemInfo.graphicsDeviceType,
				QualitySettings.activeColorSpace,
				Camera.main != null ? Camera.main.actualRenderingPath.ToString() : "(pas de camera)"));

			LoadShaderBundle();
			LoadTemperLut();
			LoadDustColors();
			LoadSettings();
		}

		private void LoadSettings()
		{
			DustMinPressureAtm = 0.01f;
			foreach (var node in GameDatabase.Instance.GetConfigNodes("LOSKET_SETTINGS")) {
				float value = 0f;
				if (node.TryGetValue("dustMinPressureAtm", ref value)) {
					DustMinPressureAtm = Mathf.Max(0f, value);
				}
				LosketStorms.LoadConfig(node);
			}
			LosketStorms.LoadLayers();
			Log("poussiere soulevee a partir de " + DustMinPressureAtm.ToString("0.###") +
			    " atm" + (DustMinPressureAtm <= 0f ? " (vide compris)" : ""));
		}

		/// <summary>
		/// Tolere l'ancien format sur une ligne des noeuds de config, par
		/// exemple LOSKET_BODY_DUST { body = Kerbin  color = 0.4, 0.3, 0.2 }.
		/// KSP ne coupe une ligne qu'au premier '=' : la premiere cle recoit
		/// alors "Kerbin  color = 0.4, 0.3, 0.2" et la seconde n'existe pas.
		/// Nos configs sont passees a une cle par ligne, mais des patchs tiers
		/// (LosketPatches) ont repris l'ancien format : on decoupe la valeur
		/// plutot que de les casser. Rend vrai si l'ancien format a ete
		/// reconnu ; first et second recoivent alors les deux valeurs.
		/// </summary>
		internal static bool SplitLegacyPair(string value, string secondKey,
		                                     out string first, out string second)
		{
			first = value;
			second = null;
			if (string.IsNullOrEmpty(value)) {
				return false;
			}
			var match = Regex.Match(value, @"\s+" + Regex.Escape(secondKey) + @"\s*=",
				RegexOptions.IgnoreCase);
			if (!match.Success) {
				return false;
			}
			first = value.Substring(0, match.Index).Trim();
			second = value.Substring(match.Index + match.Length).TrimEnd('}').Trim();
			return first.Length > 0 && second.Length > 0;
		}

		private void LoadDustColors()
		{
			foreach (var node in GameDatabase.Instance.GetConfigNodes("LOSKET_BODY_DUST")) {
				var body = node.GetValue("body");
				var colorText = node.GetValue("color");
				string legacyBody, legacyColor;
				if (string.IsNullOrEmpty(colorText) &&
				    SplitLegacyPair(body, "color", out legacyBody, out legacyColor)) {
					body = legacyBody;
					colorText = legacyColor;
					Log("noeud LOSKET_BODY_DUST a l'ancien format (une ligne) reconnu : " + body);
				}
				if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(colorText)) {
					LogWarning("noeud LOSKET_BODY_DUST incomplet ignore (body=" + body + ")");
					continue;
				}
				try {
					dustColors[body] = ConfigNode.ParseColor(colorText);
				} catch (Exception) {
					LogWarning("couleur illisible pour LOSKET_BODY_DUST " + body + " : " + colorText);
				}
			}
			Log(dustColors.Count + " couleur(s) de poussiere chargee(s)");
		}

		private void LoadTemperLut()
		{
			var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
			var path = Path.GetFullPath(
				Path.Combine(pluginDir, Path.Combine("..", Path.Combine("PluginData", "temper_lut.png"))));

			if (!File.Exists(path)) {
				LogWarning("LUT de revenu introuvable : " + path +
				           " - genere-la avec Tools/make_temper_lut.py");
				return;
			}

			var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
			if (!tex.LoadImage(File.ReadAllBytes(path))) {
				LogError("echec du decodage de " + path);
				Destroy(tex);
				return;
			}

			tex.wrapMode = TextureWrapMode.Clamp;
			tex.filterMode = FilterMode.Bilinear;
			TemperLut = tex;
			Log("LUT de revenu chargee (" + tex.width + "x" + tex.height + ")");
		}

		private void LoadShaderBundle()
		{
			var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
			var shaderDir = Path.GetFullPath(Path.Combine(pluginDir, Path.Combine("..", "Shaders")));

			if (!Directory.Exists(shaderDir)) {
				LogWarning("dossier de shaders introuvable : " + shaderDir);
				return;
			}

			// On parcourt tout le dossier plutot que de viser un seul nom de fichier.
			// Cela permet de deposer un bundle temoin d'un autre mod a cote du notre
			// pour comparer les deux dans une meme partie.
			var files = Directory.GetFiles(shaderDir);
			if (files.Length == 0) {
				LogWarning("aucun bundle dans " + shaderDir +
				           " - compile-le depuis Unity (menu Losket > Compiler le bundle de shaders).");
				return;
			}

			foreach (var path in files) {
				LoadOneBundle(path);
			}

			ShadersLoaded = shaders.Count > 0;
			Log(shaders.Count + " shader(s) disponible(s) au total");
		}

		private void LoadOneBundle(string path)
		{
			var name = Path.GetFileName(path);
			var size = new FileInfo(path).Length;

			AssetBundle bundle;
			try {
				bundle = AssetBundle.LoadFromFile(path);
			} catch (Exception e) {
				LogError(name + " (" + size + " o) : exception - " + e.Message);
				return;
			}

			if (bundle == null) {
				LogError(name + " (" + size + " o) : LoadFromFile a renvoye null");
				return;
			}

			// On compte tous les assets, pas seulement les shaders : un bundle temoin
			// sans shader doit quand meme apparaitre comme charge avec succes.
			var all = bundle.LoadAllAssets();
			var loaded = bundle.LoadAllAssets<Shader>();
			Log(name + " (" + size + " o) : OK, " + all.Length + " asset(s) dont "
			    + loaded.Length + " shader(s)");

			foreach (var shader in loaded) {
				shaders[shader.name] = shader;
				Log("    " + shader.name + (shader.isSupported ? "" : "  [NON SUPPORTE PAR CE GPU]"));
			}

			// Libere le fichier sur disque sans detruire les shaders deja charges.
			bundle.Unload(false);
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
