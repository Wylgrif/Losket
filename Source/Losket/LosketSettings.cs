using KSP.Localization;
using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Reglages globaux du mod, integres au menu Difficulte du jeu
	/// (Parametres > Losket). Remplace le config.cfg envisage au depart :
	/// persistance par sauvegarde et interface stock gratuites.
	///
	/// Deux booleens plutot qu'une enumeration a quatre valeurs : KSP affiche
	/// les enumerations par le nom brut de leurs membres, qui ne passe pas par
	/// Localizer. Les booleens sont rendus en cases a cocher dont le libelle,
	/// lui, est bien localise.
	/// </summary>
	public class LosketSettings : GameParameters.CustomParameterNode
	{
		public override string Title { get { return Localizer.Format("#LOC_Losket_Group"); } }
		public override string Section { get { return "Losket"; } }
		public override string DisplaySection { get { return Localizer.Format("#LOC_Losket_Group"); } }
		public override int SectionOrder { get { return 1; } }
		public override GameParameters.GameMode GameMode { get { return GameParameters.GameMode.ANY; } }
		/// <summary>Seule la perte solaire suit les prereglages de difficulte :
		/// c'est le seul reglage qui touche au jeu et pas seulement au visuel.</summary>
		public override bool HasPresets { get { return true; } }

		public override void SetDifficultyPreset(GameParameters.Preset preset)
		{
			switch (preset) {
				case GameParameters.Preset.Easy:
					solarDustLoss = 0f;
					break;
				case GameParameters.Preset.Normal:
					solarDustLoss = 0.5f;
					break;
				case GameParameters.Preset.Moderate:
					solarDustLoss = 0.7f;
					break;
				case GameParameters.Preset.Hard:
					solarDustLoss = 1f;
					break;
			}
		}

		[GameParameters.CustomParameterUI("#LOC_Losket_Set_Burn",
			toolTip = "#LOC_Losket_Set_BurnTip")]
		public bool burnByDefault = true;

		[GameParameters.CustomParameterUI("#LOC_Losket_Set_Dust",
			toolTip = "#LOC_Losket_Set_DustTip")]
		public bool dustByDefault = true;

		/// <summary>Multiplicateur de la vitesse de depot de la poussiere.
		/// Echelle logarithmique : x0.1 a x10, x1 au centre.</summary>
		[GameParameters.CustomFloatParameterUI("#LOC_Losket_Set_DustRate",
			toolTip = "#LOC_Losket_Set_DustRateTip", minValue = 0.1f, maxValue = 10f,
			logBase = 10f, stepCount = 21, displayFormat = "F2")]
		public float dustRate = 1f;

		/// <summary>Poussiere soulevee aussi dans le vide (Mun, Minmus...).
		/// Decoche, le seuil de pression de Configs/losket-settings.cfg
		/// s'applique.</summary>
		[GameParameters.CustomParameterUI("#LOC_Losket_Set_DustVacuum",
			toolTip = "#LOC_Losket_Set_DustVacuumTip")]
		public bool dustInVacuum = false;

		/// <summary>Couleur de la poussiere lue dans Configs/dust-colors.cfg
		/// par astre. Decoche, le gris-brun par defaut partout : c'est la
		/// teinte du nuage 3D de Volumetric Ground FX, pour que depot et
		/// nuage soient assortis.</summary>
		[GameParameters.CustomParameterUI("#LOC_Losket_Set_DustBodyColor",
			toolTip = "#LOC_Losket_Set_DustBodyColorTip")]
		public bool dustBodyColors = true;

		/// <summary>Part de la puissance d'un panneau solaire perdue sous une
		/// couche de poussiere complete, proportionnelle au recouvrement. 0 =
		/// la poussiere n'a aucun effet sur les panneaux. Suit le prereglage de
		/// difficulte (facile 0 %, normal 50 %, modere 70 %, difficile 100 %).
		/// Pas d'asPercentage : le menu de difficulte arrondit la valeur
		/// brute (0..1) avec displayFormat, et "F0" la ramenait a 0 ou 1. Avec
		/// "P0" il arrondit sur les pourcents et affiche lui-meme "50 %".</summary>
		[GameParameters.CustomFloatParameterUI("#LOC_Losket_Set_SolarLoss",
			toolTip = "#LOC_Losket_Set_SolarLossTip", minValue = 0f, maxValue = 1f,
			stepCount = 21, displayFormat = "P0")]
		public float solarDustLoss = 0.5f;

		/// <summary>Marques laissees par les panaches de moteurs. S'applique
		/// aux pieces dont "Affecte par" comprend la brulure.</summary>
		[GameParameters.CustomParameterUI("#LOC_Losket_Set_Exhaust",
			toolTip = "#LOC_Losket_Set_ExhaustTip")]
		public bool exhaustMarks = true;

		/// <summary>Multiplicateur de la vitesse de marquage par les panaches.</summary>
		[GameParameters.CustomFloatParameterUI("#LOC_Losket_Set_ExhaustRate",
			toolTip = "#LOC_Losket_Set_ExhaustRateTip", minValue = 0.1f, maxValue = 10f,
			logBase = 10f, stepCount = 21, displayFormat = "F2")]
		public float exhaustRate = 1f;

		[GameParameters.CustomParameterUI("#LOC_Losket_Set_DevUI",
			toolTip = "#LOC_Losket_Set_DevUITip")]
		public bool interfaceDev = false;

		/// <summary>Facteur de depot de la poussiere, 1 sans partie chargee.</summary>
		public static float DustRate()
		{
			var s = Instance;
			return s != null ? Mathf.Clamp(s.dustRate, 0.1f, 10f) : 1f;
		}

		/// <summary>Pression (atm) sous laquelle les moteurs ne soulevent pas
		/// de poussiere : 0 si la case "dans le vide" est cochee, sinon le
		/// seuil du cfg.</summary>
		public static float DustMinPressureAtm()
		{
			var s = Instance;
			return s != null && s.dustInVacuum ? 0f : LosketBootstrap.DustMinPressureAtm;
		}

		/// <summary>Vrai si la poussiere prend la couleur de l'astre (table de
		/// dust-colors.cfg), faux pour le gris-brun par defaut partout.</summary>
		public static bool DustBodyColors()
		{
			var s = Instance;
			return s == null || s.dustBodyColors;
		}

		/// <summary>Perte de puissance solaire a recouvrement complet, 0 sans
		/// partie chargee.</summary>
		public static float SolarDustLoss()
		{
			var s = Instance;
			return s != null ? Mathf.Clamp01(s.solarDustLoss) : 0f;
		}

		/// <summary>Vrai si les panaches de moteurs marquent les pieces.</summary>
		public static bool ExhaustMarks()
		{
			var s = Instance;
			return s == null || s.exhaustMarks;
		}

		/// <summary>Facteur de marquage des panaches, 1 sans partie chargee.</summary>
		public static float ExhaustRate()
		{
			var s = Instance;
			return s != null ? Mathf.Clamp(s.exhaustRate, 0.1f, 10f) : 1f;
		}

		/// <summary>Raccourci d'acces, null hors partie chargee.</summary>
		public static LosketSettings Instance
		{
			get
			{
				return HighLogic.CurrentGame != null
					? HighLogic.CurrentGame.Parameters.CustomParams<LosketSettings>()
					: null;
			}
		}

		/// <summary>Cle "Affecte par" correspondant au defaut global.</summary>
		public static string DefaultAffectedKey()
		{
			var s = Instance;
			var burn = s == null || s.burnByDefault;
			var dust = s == null || s.dustByDefault;

			if (burn && dust) {
				return LosketKeys.AffectedBoth;
			}
			if (burn) {
				return LosketKeys.AffectedBurn;
			}
			return dust ? LosketKeys.AffectedDust : LosketKeys.AffectedNone;
		}
	}
}
