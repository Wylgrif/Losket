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
		public override bool HasPresets { get { return false; } }

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

		[GameParameters.CustomParameterUI("#LOC_Losket_Set_DevUI",
			toolTip = "#LOC_Losket_Set_DevUITip")]
		public bool interfaceDev = false;

		/// <summary>Facteur de depot de la poussiere, 1 sans partie chargee.</summary>
		public static float DustRate()
		{
			var s = Instance;
			return s != null ? Mathf.Clamp(s.dustRate, 0.1f, 10f) : 1f;
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
