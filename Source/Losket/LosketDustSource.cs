using System;
using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Calcule une fois par FixedUpdate, pour tout le vaisseau, le taux de
	/// poussiere soulevee par les moteurs. Les modules de piece se contentent de
	/// lire KickRate : le cout ne croit pas avec le nombre de pieces.
	///
	/// Modele (convaincant, pas simule) :
	///  - taux proportionnel a la poussee totale des moteurs allumes ;
	///  - attenue par l'altitude radar : portee du souffle en racine de la
	///    poussee, plafond a 150 m ;
	///  - amplifie dans le vide : sans atmosphere le panache s'evase et projette
	///    la poussiere tres haut sur le vaisseau (photos Apollo), en atmosphere
	///    dense il est collime et la poussiere retombe. Contre-intuitif mais
	///    correct : plus l'astre a d'atmosphere, moins ca salit.
	/// </summary>
	public class LosketDustSource : VesselModule
	{
		/// <summary>Poussiere par seconde a la source, deja ponderee par
		/// l'altitude du vaisseau et la pression. L'attenuation PAR PIECE (sa
		/// hauteur au-dessus du sol) est appliquee par ModuleLosketDust au
		/// moment du depot.</summary>
		public float KickRate { get; private set; }

		/// <summary>Couleur du sol du corps survole (table par corps, cfg).</summary>
		public Color GroundColor { get; private set; }

		/// <summary>Verticale locale (monde), du sol vers le ciel.</summary>
		public Vector3 UpWorld { get; private set; }

		/// <summary>Reference de l'altitude radar (position du vaisseau).</summary>
		public Vector3 VesselRef { get; private set; }

		/// <summary>Altitude radar du vaisseau au tick courant.</summary>
		public float RadarAlt { get; private set; }

		/// <summary>
		/// Hauteur caracteristique du nuage de poussiere (m) : la dose par piece
		/// decroit en exp(-h/H). Dans le vide, la poussiere est projetee
		/// balistiquement tres haut (H grand, photos Apollo) ; en atmosphere
		/// dense elle est freinee et retombe vite (H petit). C'est la nuance
		/// atmospherique du systeme de proximite.
		/// </summary>
		public float ScaleHeight { get; private set; }

		/// <summary>
		/// Part "gerbe rasante" du depot en cours : ~1 dans le vide (la
		/// poussiere arrive a l'horizontale et frappe les flancs, cf. Apollo),
		/// ~0 en atmosphere dense (elle tourbillonne et retombe de partout).
		/// </summary>
		public float RingFactor { get; private set; }

		/// <summary>Intensite (0..1) de la tempete de poussiere de Volumetric
		/// Clouds autour du vaisseau, voir LosketStorms.</summary>
		public float StormIntensity { get; private set; }

		/// <summary>Couleur de la poussiere de tempete : celle de l'astre.</summary>
		public Color StormColor { get; private set; }

		/// <summary>La couverture n'est relue qu'un tick sur StormStride :
		/// une tempete evolue a l'echelle de la minute.</summary>
		private const int StormStride = 10;

		private int stormTick;

		public override Activation GetActivation()
		{
			return Activation.LoadedVessels | Activation.FlightScene;
		}

		private void FixedUpdate()
		{
			KickRate = 0f;

			if (vessel == null || !vessel.loaded || vessel.parts == null) {
				StormIntensity = 0f;
				return;
			}
			var body = vessel.mainBody;

			// Avant tous les filtres du souffle des moteurs : une tempete salit
			// aussi un vaisseau pose sur une piste, en vol ou en acceleration
			// du temps (vaisseau sur rails).
			if (++stormTick % StormStride == 0 || body == null) {
				var wasInStorm = StormIntensity > 0f;
				StormIntensity = LosketStorms.Intensity(body, vessel.CoM);
				StormColor = LosketBootstrap.GetDustColor(body);
				if (wasInStorm != StormIntensity > 0f) {
					LosketBootstrap.Log("tempetes : " + vessel.vesselName +
					                    (wasInStorm ? " sort de la tempete" : " entre dans une tempete (" +
					                                  StormIntensity.ToString("0.00") + ")"));
				}
			}

			if (body == null || !body.hasSolidSurface || vessel.Splashed) {
				return;
			}

			// Moteurs au-dessus de l'eau : de l'ecume, pas de la poussiere. Le
			// drapeau ocean du corps couvre aussi les planetes moddees ; un
			// terrain sous le niveau de la mer signifie que la surface sous le
			// vaisseau est de l'eau.
			if (body.ocean && vessel.terrainAltitude < 0) {
				return;
			}

			// Surfaces artificielles : pas de tir, pistes, toits — propres, rien
			// a soulever. Trois filets complementaires :
			//  1. pre-lancement : par definition sur une installation ;
			//  2. pose sur une installation nommee (landedAt est vide sur le
			//     terrain naturel) ;
			//  3. en survol, un rayon vers le sol : si le collider touche n'est
			//     pas un quad de terrain PQS, c'est du bati (KSC, statics de
			//     mods type Kerbal Konstructs).
			if (vessel.situation == Vessel.Situations.PRELAUNCH) {
				return;
			}
			if (vessel.Landed && !string.IsNullOrEmpty(vessel.landedAt)) {
				return;
			}
			var up = (vessel.transform.position - body.position).normalized;
			RaycastHit hit;
			if (Physics.Raycast(vessel.CoM + up * 2f, -up, out hit, 300f,
				    1 << 15, QueryTriggerInteraction.Ignore) &&
			    hit.collider.GetComponentInParent<PQ>() == null) {
				return;
			}

			var pressureAtm = (float)(vessel.staticPressurekPa / 101.325);

			// Sous ce seuil (Configs/losket-settings.cfg), rien n'est souleve.
			// Par defaut cela coupe la poussiere dans le vide : le depot en
			// gerbe rasante y est correct techniquement mais pas convaincant
			// a l'usage. La case "Poussiere dans le vide" des reglages leve le
			// seuil.
			if (pressureAtm < LosketSettings.DustMinPressureAtm()) {
				return;
			}

			var thrust = 0f;
			var rotorWash = 0f;
			for (var i = 0; i < vessel.parts.Count; i++) {
				var modules = vessel.parts[i].Modules;
				for (var j = 0; j < modules.Count; j++) {
					var engine = modules[j] as ModuleEngines;
					if (engine != null) {
						thrust += engine.finalThrust;
						continue;
					}
					// Rotors Breaking Ground : le souffle d'helice est estime par
					// couple x regime. Il n'existe qu'en atmosphere - sans air,
					// pas de flux descendant, quel que soit le regime. Constante
					// calibree a l'oeil en jeu (l'ancienne valeur 0.001 salissait
					// ~14x trop vite pour etre credible).
					var rotor = modules[j] as Expansions.Serenity.ModuleRoboticServoRotor;
					if (rotor != null && pressureAtm > 0.005f) {
						rotorWash += rotor.maxTorque * Mathf.Abs(rotor.currentRPM) * 0.00007f;
					}
				}
			}
			if (thrust <= 0f && rotorWash <= 0f) {
				return;
			}

			// Portee du souffle : ~25 m pour un atterrisseur type LEM (45 kN),
			// plafonnee pour les monstres a 4 MN.
			var reach = Mathf.Clamp(4f * Mathf.Sqrt(thrust + rotorWash), 10f, 150f);
			var alt = (float)vessel.radarAltitude;
			if (alt < 0f || alt > reach) {
				return;
			}
			var falloff = 1f - alt / reach;
			falloff *= falloff;

			// Le facteur de vide (panache de fusee qui s'evase sans atmosphere)
			// ne s'applique qu'aux moteurs : le souffle d'une helice est de l'air
			// pousse directement, l'atmosphere est sa condition d'existence, pas
			// son frein.
			var vacuumFactor = 1f / (1f + 2f * pressureAtm);

			KickRate = (thrust * vacuumFactor + rotorWash) * falloff;
			GroundColor = LosketBootstrap.GetDustColor(body);
			UpWorld = (vessel.transform.position - body.position).normalized;
			VesselRef = vessel.CoM;
			RadarAlt = alt;
			// Coefficient d'atmosphere releve apres essais : la poussiere montait
			// trop haut sur les vaisseaux sous atmosphere. Kerbin ~3 m (au ras
			// des tuyeres), Duna ~22 m, vide 40 m (projection balistique Apollo).
			ScaleHeight = Mathf.Max(2.5f, 40f / (1f + 12f * pressureAtm));
			RingFactor = 1f / (1f + 4f * pressureAtm);
		}
	}
}
