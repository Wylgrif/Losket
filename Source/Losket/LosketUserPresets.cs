using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Un style de brulure complet : les champs que la fenetre de reglages
	/// edite sur un ModuleLosketBurn, plus un nom. Sert de forme d'echange
	/// entre le module, les fichiers de PluginData/Presets et le
	/// presse-papiers.
	/// </summary>
	public class LosketBurnStyle
	{
		public const string NodeName = "LOSKET_BURN_PRESET";

		public string name = "";
		public float sootR, sootG, sootB;
		public float temperIntensity;
		public float sharpness;
		public float streak;
		public float noiseScale;
		public float pattern;
		public float bleach;
		public bool matte;
		public float tileSize, tileLeak, tileBroken, tileLength, tileFan, tileEdgeWear;

		public static LosketBurnStyle From(ModuleLosketBurn m, string name)
		{
			return new LosketBurnStyle {
				name = name,
				sootR = m.sootR, sootG = m.sootG, sootB = m.sootB,
				temperIntensity = m.temperIntensity,
				sharpness = m.sharpness,
				streak = m.streak,
				noiseScale = m.noiseScale,
				pattern = m.pattern,
				bleach = m.bleach,
				matte = m.matte,
				tileSize = m.tileSize,
				tileLeak = m.tileLeak, tileBroken = m.tileBroken,
				tileLength = m.tileLength, tileFan = m.tileFan,
				tileEdgeWear = m.tileEdgeWear,
			};
		}

		public void ApplyTo(ModuleLosketBurn m)
		{
			m.sootR = sootR; m.sootG = sootG; m.sootB = sootB;
			m.temperIntensity = temperIntensity;
			m.sharpness = sharpness;
			m.streak = streak;
			m.noiseScale = noiseScale;
			m.pattern = pattern;
			m.bleach = bleach;
			m.matte = matte;
			m.tileSize = tileSize;
			m.tileLeak = tileLeak; m.tileBroken = tileBroken;
			m.tileLength = tileLength; m.tileFan = tileFan;
			m.tileEdgeWear = tileEdgeWear;
		}

		public ConfigNode ToNode()
		{
			var n = new ConfigNode(NodeName);
			n.AddValue("name", name);
			n.AddValue("sootR", sootR);
			n.AddValue("sootG", sootG);
			n.AddValue("sootB", sootB);
			n.AddValue("temperIntensity", temperIntensity);
			n.AddValue("sharpness", sharpness);
			n.AddValue("streak", streak);
			n.AddValue("noiseScale", noiseScale);
			n.AddValue("pattern", pattern);
			n.AddValue("bleach", bleach);
			n.AddValue("matte", matte);
			n.AddValue("tileSize", tileSize);
			n.AddValue("tileLeak", tileLeak);
			n.AddValue("tileBroken", tileBroken);
			n.AddValue("tileLength", tileLength);
			n.AddValue("tileFan", tileFan);
			n.AddValue("tileEdgeWear", tileEdgeWear);
			return n;
		}

		/// <summary>Lit un noeud LOSKET_BURN_PRESET ; null si le nom manque.
		/// Les valeurs absentes gardent celles du preregle Suie, pour qu'un
		/// fichier ecrit a la main ou par une version plus ancienne reste
		/// lisible.</summary>
		public static LosketBurnStyle FromNode(ConfigNode n)
		{
			if (n == null) {
				return null;
			}
			var s = new LosketBurnStyle {
				sootR = 0.05f, sootG = 0.048f, sootB = 0.045f,
				temperIntensity = 0.15f, sharpness = 1.3f, streak = 1f,
				noiseScale = 3f, pattern = 0f, bleach = 0f, matte = true,
				tileSize = 0.25f, tileLeak = 0.12f, tileBroken = 0.01f,
				tileLength = 1.5f, tileFan = 6f, tileEdgeWear = 1f,
			};
			s.name = LosketUserPresets.CleanName(n.GetValue("name"));
			if (string.IsNullOrEmpty(s.name)) {
				return null;
			}
			n.TryGetValue("sootR", ref s.sootR);
			n.TryGetValue("sootG", ref s.sootG);
			n.TryGetValue("sootB", ref s.sootB);
			n.TryGetValue("temperIntensity", ref s.temperIntensity);
			n.TryGetValue("sharpness", ref s.sharpness);
			n.TryGetValue("streak", ref s.streak);
			n.TryGetValue("noiseScale", ref s.noiseScale);
			n.TryGetValue("pattern", ref s.pattern);
			n.TryGetValue("bleach", ref s.bleach);
			n.TryGetValue("matte", ref s.matte);
			n.TryGetValue("tileSize", ref s.tileSize);
			n.TryGetValue("tileLeak", ref s.tileLeak);
			n.TryGetValue("tileBroken", ref s.tileBroken);
			n.TryGetValue("tileLength", ref s.tileLength);
			n.TryGetValue("tileFan", ref s.tileFan);
			n.TryGetValue("tileEdgeWear", ref s.tileEdgeWear);
			return s;
		}
	}

	/// <summary>
	/// Preregles de brulure enregistres par le joueur.
	///
	/// Un fichier par preregle dans GameData/Losket/PluginData/Presets/ :
	/// PluginData n'est pas lu par ModuleManager, et un fichier par preregle
	/// se partage tel quel (le deposer chez un autre joueur suffit). Le
	/// presse-papiers sert d'export/import sans quitter le jeu : le texte
	/// copie est le meme noeud que le fichier.
	///
	/// Les pieces stockent la cle "User:nom" (LosketKeys). Un preregle
	/// supprime ne casse rien : les valeurs vivent dans la piece, seule la
	/// mention du nom disparait.
	/// </summary>
	public static class LosketUserPresets
	{
		public const string Folder = "GameData/Losket/PluginData/Presets/";

		private static List<LosketBurnStyle> presets;

		/// <summary>Leve apres tout enregistrement, suppression ou rechargement.</summary>
		public static event Action Changed;

		public static string Directory
		{
			get { return Path.Combine(KSPUtil.ApplicationRootPath, Folder); }
		}

		public static IList<LosketBurnStyle> All
		{
			get
			{
				if (presets == null) {
					Load();
				}
				return presets;
			}
		}

		public static List<string> Names()
		{
			var all = All;
			var names = new List<string>(all.Count);
			for (var i = 0; i < all.Count; i++) {
				names.Add(all[i].name);
			}
			return names;
		}

		public static LosketBurnStyle Find(string name)
		{
			var all = All;
			for (var i = 0; i < all.Count; i++) {
				if (string.Equals(all[i].name, name, StringComparison.OrdinalIgnoreCase)) {
					return all[i];
				}
			}
			return null;
		}

		/// <summary>Relit le dossier. Les fichiers illisibles sont journalises
		/// et ignores.</summary>
		public static void Load()
		{
			var loaded = new List<LosketBurnStyle>();
			var dir = Directory;
			if (System.IO.Directory.Exists(dir)) {
				var files = System.IO.Directory.GetFiles(dir, "*.cfg");
				Array.Sort(files, StringComparer.OrdinalIgnoreCase);
				foreach (var f in files) {
					try {
						var root = ConfigNode.Load(f);
						var style = LosketBurnStyle.FromNode(root != null
							? root.GetNode(LosketBurnStyle.NodeName) : null);
						if (style == null) {
							LosketBootstrap.Log("Preregle ignore (pas de noeud " +
								LosketBurnStyle.NodeName + " ou pas de nom) : " + f);
							continue;
						}
						if (loaded.Exists(p => string.Equals(p.name, style.name,
							    StringComparison.OrdinalIgnoreCase))) {
							LosketBootstrap.Log("Preregle en double ignore : " + f);
							continue;
						}
						loaded.Add(style);
					} catch (Exception e) {
						LosketBootstrap.Log("Preregle illisible " + f + " : " + e.Message);
					}
				}
			}
			presets = loaded;
			SortAndNotify();
		}

		/// <summary>Enregistre (ou remplace) un preregle et l'ecrit sur disque.
		/// Renvoie faux si le nom est vide ou si l'ecriture echoue.</summary>
		public static bool Save(LosketBurnStyle style)
		{
			style.name = CleanName(style.name);
			if (string.IsNullOrEmpty(style.name)) {
				return false;
			}
			try {
				System.IO.Directory.CreateDirectory(Directory);
				var root = new ConfigNode();
				root.AddNode(style.ToNode());
				if (!root.Save(FileFor(style.name), "Preregle de brulure Losket")) {
					return false;
				}
			} catch (Exception e) {
				LosketBootstrap.Log("Enregistrement du preregle '" + style.name +
					"' impossible : " + e.Message);
				return false;
			}
			var existing = Find(style.name);
			if (existing != null) {
				presets.Remove(existing);
			}
			presets.Add(style);
			SortAndNotify();
			return true;
		}

		public static bool Delete(string name)
		{
			var existing = Find(name);
			if (existing == null) {
				return false;
			}
			try {
				var f = FileFor(existing.name);
				if (File.Exists(f)) {
					File.Delete(f);
				}
			} catch (Exception e) {
				LosketBootstrap.Log("Suppression du preregle '" + name + "' impossible : " +
					e.Message);
				return false;
			}
			presets.Remove(existing);
			SortAndNotify();
			return true;
		}

		/// <summary>Copie le preregle dans le presse-papiers, sous la forme
		/// exacte du fichier : collable chez un autre joueur ou dans un forum.</summary>
		public static void CopyToClipboard(LosketBurnStyle style)
		{
			GUIUtility.systemCopyBuffer = style.ToNode().ToString();
		}

		/// <summary>Lit un preregle depuis le presse-papiers ; null si le
		/// texte n'en contient pas.</summary>
		public static LosketBurnStyle FromClipboard()
		{
			var text = GUIUtility.systemCopyBuffer;
			if (string.IsNullOrEmpty(text) || text.IndexOf(LosketBurnStyle.NodeName,
				    StringComparison.Ordinal) < 0) {
				return null;
			}
			try {
				var root = ConfigNode.Parse(text);
				if (root == null) {
					return null;
				}
				// Selon que le texte commence par le noeud ou par ses valeurs,
				// Parse rend le noeud lui-meme ou un parent anonyme.
				var node = root.name == LosketBurnStyle.NodeName
					? root : root.GetNode(LosketBurnStyle.NodeName);
				return LosketBurnStyle.FromNode(node);
			} catch (Exception e) {
				LosketBootstrap.Log("Presse-papiers illisible : " + e.Message);
				return null;
			}
		}

		/// <summary>Ouvre le dossier des preregles dans l'explorateur du
		/// systeme, pour y deposer un fichier recu ou en recuperer un.</summary>
		public static void OpenFolder()
		{
			try {
				System.IO.Directory.CreateDirectory(Directory);
				var full = Path.GetFullPath(Directory).Replace('\\', '/');
				Application.OpenURL("file:///" + full);
			} catch (Exception e) {
				LosketBootstrap.Log("Ouverture du dossier des preregles impossible : " + e.Message);
			}
		}

		/// <summary>Nom affichable et utilisable comme nom de fichier : espaces
		/// rognes, caracteres interdits retires, 40 caracteres au plus.</summary>
		public static string CleanName(string raw)
		{
			if (raw == null) {
				return "";
			}
			var invalid = Path.GetInvalidFileNameChars();
			var sb = new System.Text.StringBuilder(raw.Length);
			foreach (var c in raw) {
				if (Array.IndexOf(invalid, c) < 0 && !char.IsControl(c)) {
					sb.Append(c);
				}
			}
			var s = sb.ToString().Trim();
			if (s.Length > 40) {
				s = s.Substring(0, 40).Trim();
			}
			return s;
		}

		private static string FileFor(string name)
		{
			return Path.Combine(Directory, name + ".cfg");
		}

		private static void SortAndNotify()
		{
			presets.Sort((a, b) => string.Compare(a.name, b.name,
				StringComparison.OrdinalIgnoreCase));
			var h = Changed;
			if (h != null) {
				h();
			}
		}
	}
}
