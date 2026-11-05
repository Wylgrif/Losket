using KSP.Localization;

namespace Losket
{
	/// <summary>
	/// Cles stables des listes deroulantes, et leurs libelles traduits.
	///
	/// Les champs persistants stockent TOUJOURS ces cles, jamais le texte
	/// affiche : une sauvegarde creee en francais doit rester lisible en
	/// chinois. UI_ChooseOption separe justement `options` (ce qui est stocke)
	/// de `display` (ce qui est montre).
	/// </summary>
	public static class LosketKeys
	{
		public const string AffectedNone = "None";
		public const string AffectedBurn = "Burn";
		public const string AffectedDust = "Dust";
		public const string AffectedBoth = "Both";

		public const string PresetSoot = "Soot";
		public const string PresetSootGrey = "SootGrey";
		public const string PresetNatural = "Natural";
		public const string PresetMetal = "Metal";
		public const string PresetStreaks = "Streaks";
		public const string PresetTiles = "Tiles";
		public const string PresetCustom = "Custom";

		/// <summary>Prefixe des cles de preregles enregistres par le joueur :
		/// "User:Mon reglage". Le nom qui suit est celui du fichier dans
		/// PluginData/Presets.</summary>
		public const string UserPresetPrefix = "User:";

		public static readonly string[] AffectedOptions = {
			AffectedNone, AffectedBurn, AffectedDust, AffectedBoth,
		};

		/// <summary>Preregles livres avec le mod, dans l'ordre d'affichage.</summary>
		public static readonly string[] BuiltInPresets = {
			PresetSoot, PresetSootGrey, PresetNatural, PresetMetal, PresetStreaks, PresetTiles,
		};

		public static string UserPresetKey(string name)
		{
			return UserPresetPrefix + name;
		}

		public static bool IsUserPreset(string key)
		{
			return key != null && key.StartsWith(UserPresetPrefix, System.StringComparison.Ordinal);
		}

		public static string UserPresetName(string key)
		{
			return IsUserPreset(key) ? key.Substring(UserPresetPrefix.Length) : null;
		}

		/// <summary>Cles de la liste "Preregle" du PAW : les preregles livres,
		/// ceux du joueur, puis Personnalise. Recalculee a chaque appel : la
		/// liste du joueur change quand il enregistre ou supprime.</summary>
		public static string[] PresetOptions()
		{
			var users = LosketUserPresets.Names();
			var keys = new string[BuiltInPresets.Length + users.Count + 1];
			var i = 0;
			foreach (var k in BuiltInPresets) {
				keys[i++] = k;
			}
			foreach (var n in users) {
				keys[i++] = UserPresetKey(n);
			}
			keys[i] = PresetCustom;
			return keys;
		}

		public static string[] AffectedDisplay()
		{
			return new[] {
				Localizer.Format("#LOC_Losket_Affected_None"),
				Localizer.Format("#LOC_Losket_Affected_Burn"),
				Localizer.Format("#LOC_Losket_Affected_Dust"),
				Localizer.Format("#LOC_Losket_Affected_Both"),
			};
		}

		/// <summary>Libelles de PresetOptions(), dans le meme ordre.</summary>
		public static string[] PresetDisplay()
		{
			var keys = PresetOptions();
			var display = new string[keys.Length];
			for (var i = 0; i < keys.Length; i++) {
				display[i] = PresetLabel(keys[i]);
			}
			return display;
		}

		/// <summary>Libelle traduit d'une cle de preregle ; le nom brut pour
		/// un preregle du joueur.</summary>
		public static string PresetLabel(string key)
		{
			switch (key) {
				case PresetSoot: return Localizer.Format("#LOC_Losket_Preset_Soot");
				case PresetSootGrey: return Localizer.Format("#LOC_Losket_Preset_SootGrey");
				case PresetNatural: return Localizer.Format("#LOC_Losket_Preset_Natural");
				case PresetMetal: return Localizer.Format("#LOC_Losket_Preset_Metal");
				case PresetStreaks: return Localizer.Format("#LOC_Losket_Preset_Streaks");
				case PresetTiles: return Localizer.Format("#LOC_Losket_Preset_Tiles");
				case PresetCustom: return Localizer.Format("#LOC_Losket_Preset_Custom");
				default: return IsUserPreset(key) ? UserPresetName(key) : key;
			}
		}

		/// <summary>
		/// Rattrape les valeurs des sauvegardes anterieures a la localisation,
		/// qui stockaient le libelle francais en clair.
		/// </summary>
		public static string MigrateAffected(string stored)
		{
			switch (stored) {
				case "Rien": return AffectedNone;
				case "Brulure": return AffectedBurn;
				case "Poussiere": return AffectedDust;
				case "Brulure & poussiere": return AffectedBoth;
				default: return stored;
			}
		}

		public static string MigratePreset(string stored)
		{
			switch (stored) {
				case "Suie": return PresetSoot;
				case "Metal": return PresetMetal;
				case "Stries": return PresetStreaks;
				case "Personnalise": return PresetCustom;
				default: return stored;
			}
		}
	}
}
