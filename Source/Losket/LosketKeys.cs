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
		public const string PresetMetal = "Metal";
		public const string PresetStreaks = "Streaks";
		public const string PresetCustom = "Custom";

		public static readonly string[] AffectedOptions = {
			AffectedNone, AffectedBurn, AffectedDust, AffectedBoth,
		};

		public static readonly string[] PresetOptions = {
			PresetSoot, PresetMetal, PresetStreaks, PresetCustom,
		};

		public static string[] AffectedDisplay()
		{
			return new[] {
				Localizer.Format("#LOC_Losket_Affected_None"),
				Localizer.Format("#LOC_Losket_Affected_Burn"),
				Localizer.Format("#LOC_Losket_Affected_Dust"),
				Localizer.Format("#LOC_Losket_Affected_Both"),
			};
		}

		public static string[] PresetDisplay()
		{
			return new[] {
				Localizer.Format("#LOC_Losket_Preset_Soot"),
				Localizer.Format("#LOC_Losket_Preset_Metal"),
				Localizer.Format("#LOC_Losket_Preset_Streaks"),
				Localizer.Format("#LOC_Losket_Preset_Custom"),
			};
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
