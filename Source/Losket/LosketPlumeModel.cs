using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Geometrie et intensite d'un panache de moteur. Calcul pur, sans etat ni
	/// acces au jeu : tout ce qui depend de KSP est resolu par LosketPlumeField.
	///
	/// Modele (convaincant, pas simule) :
	///  - le panache est un cone qui part de la tuyere. La poussee ne regle que
	///    sa TAILLE (rayon de tuyere, portee) : a la sortie de tuyere le flux
	///    est le meme pour un Sepatron et un Mainsail, seul le second porte loin ;
	///  - sans atmosphere le jet s'evase (~30 degres), sous 1 atm il reste
	///    collime (~10 degres). Meme nuance que pour la poussiere ;
	///  - l'intensite, entre 0 et 1, est le produit de trois termes : profil
	///    radial gaussien, dilution quand le cone s'elargit, extinction
	///    lineaire jusqu'a la portee.
	/// </summary>
	public static class LosketPlumeModel
	{
		public struct Plume
		{
			/// <summary>Sortie de tuyere (monde).</summary>
			public Vector3 Origin;

			/// <summary>Sens de deplacement des gaz (monde, norme).</summary>
			public Vector3 Axis;

			public float Length;
			public float NozzleRadius;
			public float TanHalfAngle;
		}

		/// <summary>Au-dela de ce multiple du rayon du cone, plus rien.</summary>
		public const float RadialCutoff = 1.6f;

		/// <summary>Rayon de tuyere estime (m) : ~0.25 m pour 20 kN, ~0.45 m
		/// pour 200 kN, ~0.9 m pour 1500 kN.</summary>
		public static float NozzleRadius(float thrustKN)
		{
			return 0.15f + 0.02f * Mathf.Sqrt(Mathf.Max(0f, thrustKN));
		}

		/// <summary>Portee du jet (m) : ~8 m pour un Sepatron, ~28 m pour
		/// 200 kN, plafonnee a 40 m.</summary>
		public static float Length(float thrustKN)
		{
			return Mathf.Clamp(2f * Mathf.Sqrt(Mathf.Max(0f, thrustKN)), 2f, 40f);
		}

		/// <summary>Tangente du demi-angle du cone selon la pression ambiante.</summary>
		public static float TanHalfAngle(float pressureAtm)
		{
			return 0.12f + 0.45f / (1f + 8f * Mathf.Max(0f, pressureAtm));
		}

		public static Plume Make(Vector3 origin, Vector3 axis, float thrustKN, float pressureAtm)
		{
			return new Plume {
				Origin = origin,
				Axis = axis.normalized,
				Length = Length(thrustKN),
				NozzleRadius = NozzleRadius(thrustKN),
				TanHalfAngle = TanHalfAngle(pressureAtm),
			};
		}

		/// <summary>Rayon du cone a la distance axiale d.</summary>
		public static float RadiusAt(ref Plume plume, float axial)
		{
			return plume.NozzleRadius + Mathf.Max(0f, axial) * plume.TanHalfAngle;
		}

		/// <summary>
		/// Vrai si une sphere peut toucher le panache. Test large, sans faux
		/// negatif : il ne sert qu'a ecarter les pieces lointaines avant les
		/// calculs couteux.
		/// </summary>
		public static bool MayTouch(ref Plume plume, Vector3 center, float radius,
			out float axial)
		{
			var rel = center - plume.Origin;
			axial = Vector3.Dot(rel, plume.Axis);
			if (axial < -radius || axial > plume.Length + radius) {
				return false;
			}
			var radial = (rel - plume.Axis * axial).magnitude;
			return radial <= RadialCutoff * RadiusAt(ref plume, axial + radius) + radius;
		}

		/// <summary>
		/// Intensite du jet en un point, entre 0 et 1. coneRadius recoit le
		/// rayon du cone a cette distance (sert a dimensionner la tache).
		/// </summary>
		public static float Intensity(ref Plume plume, Vector3 point, out float coneRadius)
		{
			var rel = point - plume.Origin;
			var axial = Vector3.Dot(rel, plume.Axis);
			coneRadius = RadiusAt(ref plume, axial);
			if (axial < 0f || axial >= plume.Length) {
				return 0f;
			}
			var radial = (rel - plume.Axis * axial).magnitude;
			if (radial > RadialCutoff * coneRadius) {
				return 0f;
			}
			var x = radial / coneRadius;
			var profile = Mathf.Exp(-2f * x * x);
			var dilution = plume.NozzleRadius / coneRadius;
			var extinction = 1f - axial / plume.Length;
			return profile * dilution * extinction;
		}

		/// <summary>
		/// Rayon de la tache laissee par le jet a cet endroit. Le profil
		/// exp(-2 (r/R)^2) equivaut a une gaussienne exp(-(r/s)^2) de rayon
		/// s = R / sqrt(2).
		/// </summary>
		public static float SpotRadius(float coneRadius)
		{
			return Mathf.Clamp(coneRadius * 0.7071f, 0.2f, 5f);
		}
	}
}
