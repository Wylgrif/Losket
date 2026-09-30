using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Detecte, en vol, ce que frappent les panaches des moteurs allumes et
	/// depose la dose correspondante sur les pieces touchees - celles des
	/// autres vaisseaux comme celles du vaisseau du moteur. Un seul composant
	/// pour toute la scene : un jet ne connait pas les frontieres entre
	/// vaisseaux.
	///
	/// Lecture seule sur les moteurs (poussee, tuyeres, etat d'allumage) et nos
	/// propres rayons : rien du fonctionnement de KSP n'est modifie, ni la
	/// chauffe ni les degats d'echappement du jeu.
	///
	/// Par tuyere allumee :
	///  1. la tuyere se marque elle-meme (revenu par temps de fonctionnement) ;
	///  2. les pieces dont la sphere englobante touche le cone sont retenues ;
	///  3. pour chacune, l'intensite est evaluee en quelques stations le long
	///     de l'axe, au point de sa surface le plus proche du jet ;
	///  4. un rayon tire de la tuyere vers le point le plus chaud verifie
	///     qu'aucune autre piece, ni le sol, ni un batiment ne fait ecran.
	///
	/// Cout : les moteurs sont traites un tick sur Stride, en etale ; seuls les
	/// moteurs allumes comptent, et le tri par sphere est de l'arithmetique.
	/// </summary>
	[KSPAddon(KSPAddon.Startup.Flight, false)]
	public class LosketPlumeField : MonoBehaviour
	{
		/// <summary>Un moteur est traite un tick de physique sur Stride.</summary>
		private const int Stride = 4;

		/// <summary>Stations d'evaluation le long de l'axe, par piece.</summary>
		private const int Stations = 4;

		/// <summary>Intensite sous laquelle une piece n'est pas marquee.</summary>
		private const float MinIntensity = 0.002f;

		/// <summary>Rayon de la tache d'une tuyere marquee par elle-meme, en
		/// multiple du rayon de tuyere estime.</summary>
		private const float SelfSpotScale = 1f;

		/// <summary>Poids de la direction "vers l'axe du jet" dans le cote
		/// expose. Un jet rasant marque la face tournee vers lui, pas celle
		/// qui regarde la tuyere.</summary>
		private const float TowardAxisWeight = 1.5f;

		/// <summary>Pieces (calques par defaut de KSP) + terrain et batiments
		/// (calque 15), qui font ecran sans jamais etre marques.</summary>
		private static readonly int OcclusionMask = LayerUtil.DefaultEquivalent | (1 << 15);

		private static readonly RaycastHit[] hits = new RaycastHit[32];
		private static readonly Vector3[] stationPoint = new Vector3[Stations];
		private static readonly float[] stationFlux = new float[Stations];
		private static readonly float[] stationSpot = new float[Stations];

		private int tick;

		private void FixedUpdate()
		{
			if (!FlightGlobals.ready || !LosketSettings.ExhaustMarks()) {
				return;
			}
			tick++;

			var dt = TimeWarp.fixedDeltaTime * Stride;
			var rate = LosketSettings.ExhaustRate();
			var modules = ModuleLosketExhaust.Loaded;

			for (var i = 0; i < modules.Count; i++) {
				var source = modules[i];
				if (source == null || source.EngineCount == 0) {
					continue;
				}
				if ((tick + i) % Stride != 0) {
					continue;
				}
				var part = source.part;
				if (part == null || part.packed || source.vessel == null) {
					continue;
				}
				for (var e = 0; e < source.EngineCount; e++) {
					if (!source.EngineIsCold(e)) {
						ProcessEngine(source, source.Engine(e), dt, rate);
					}
				}
			}
		}

		private static void ProcessEngine(ModuleLosketExhaust source, ModuleEngines engine,
			float dt, float rate)
		{
			if (engine == null || !engine.EngineIgnited || engine.finalThrust <= 0.01f) {
				return;
			}
			var transforms = engine.thrustTransforms;
			if (transforms == null || transforms.Count == 0) {
				return;
			}
			var multipliers = engine.thrustTransformMultipliers;
			var evenShare = 1f / transforms.Count;
			var pressureAtm = (float)(source.vessel.staticPressurekPa / 101.325);
			var throttle = Mathf.Clamp01(engine.finalThrust / Mathf.Max(engine.maxThrust, 0.001f));

			for (var t = 0; t < transforms.Count; t++) {
				var nozzle = transforms[t];
				if (nozzle == null) {
					continue;
				}
				var share = multipliers != null && multipliers.Count == transforms.Count
					? multipliers[t]
					: evenShare;
				var thrust = engine.finalThrust * share;
				if (thrust <= 0.01f) {
					continue;
				}

				// Le jet part le long de +Z de la tuyere : c'est la direction du
				// rayon des degats d'echappement du jeu.
				var plume = LosketPlumeModel.Make(nozzle.position, nozzle.forward,
					thrust, pressureAtm);

				// 1. La tuyere elle-meme. Ponderee par la part de poussee : un
				// moteur a quatre tuyeres ne bleuit pas quatre fois plus vite,
				// mais sa tache couvre les quatre.
				if (source.AffectedByExhaust) {
					source.Deposit(source.selfRate * throttle * share * dt * rate, throttle,
						plume.Axis, plume.Axis, plume.Origin,
						SelfSpotScale * plume.NozzleRadius, true);
				}

				// 2 a 4. Tout ce que le jet rencontre.
				var modules = ModuleLosketExhaust.Loaded;
				for (var i = 0; i < modules.Count; i++) {
					var target = modules[i];
					if (target == null || ReferenceEquals(target, source)) {
						continue;
					}
					var targetPart = target.part;
					if (targetPart == null || targetPart.packed) {
						continue;
					}
					// Tri grossier avant tout calcul : a plus de la portee
					// maximale d'un jet augmentee d'une tres grande piece, rien
					// a faire.
					if ((targetPart.transform.position - plume.Origin).sqrMagnitude >
					    (plume.Length + 30f) * (plume.Length + 30f)) {
						continue;
					}
					if (!target.AffectedByExhaust) {
						continue;
					}
					Expose(ref plume, source.part, target, dt * rate);
				}
			}
		}

		/// <summary>Evalue et depose la dose recue par une piece cible.</summary>
		private static void Expose(ref LosketPlumeModel.Plume plume, Part sourcePart,
			ModuleLosketExhaust target, float dt)
		{
			target.EnsureGeometry();
			if (!target.HasColliders) {
				return;
			}

			float axial;
			var radius = target.BoundRadius;
			if (!LosketPlumeModel.MayTouch(ref plume, target.WorldCenter, radius, out axial)) {
				return;
			}

			// Stations le long de l'axe, sur la portion que la piece recouvre.
			var lo = Mathf.Max(0f, axial - radius);
			var hi = Mathf.Min(plume.Length, axial + radius);
			if (hi <= lo) {
				return;
			}

			var best = -1;
			var total = 0f;
			var entryKnown = false;
			var entryFound = false;
			var entry = Vector3.zero;
			for (var k = 0; k < Stations; k++) {
				var d = Mathf.Lerp(lo, hi, (k + 0.5f) / Stations);
				var q = plume.Origin + plume.Axis * d;
				var p = target.ClosestSurfacePoint(q);
				if ((p - q).sqrMagnitude < 1e-6f) {
					// La station est DANS la piece : l'axe du jet la traverse
					// (etage inferieur sous la tuyere). Le point frappe est
					// celui ou l'axe entre dans la piece, le meme pour toutes
					// les stations interieures.
					if (!entryKnown) {
						entryKnown = true;
						entryFound = AxisEntry(ref plume, target.part, out entry);
					}
					if (!entryFound) {
						stationFlux[k] = 0f;
						continue;
					}
					p = entry;
				}
				float coneRadius;
				var flux = LosketPlumeModel.Intensity(ref plume, p, out coneRadius);
				stationPoint[k] = p;
				stationFlux[k] = flux;
				stationSpot[k] = LosketPlumeModel.SpotRadius(coneRadius);
				total += flux;
				if (flux > 0f && (best < 0 || flux > stationFlux[best])) {
					best = k;
				}
			}
			if (best < 0 || stationFlux[best] < MinIntensity) {
				return;
			}
			if (Occluded(plume.Origin, stationPoint[best], sourcePart, target.part)) {
				return;
			}

			// La dose est celle du point le plus chaud : c'est un flux par
			// unite de surface, pas une puissance captee - une petite piece
			// dans l'axe noircit aussi vite que la meme zone d'une grande.
			// Elle est repartie entre les stations au prorata de leur
			// intensite : le centre et le rayon de la tache couvrent ainsi
			// toute la longueur lechee par le jet.
			var peak = stationFlux[best];
			var ddose = peak * dt;
			for (var k = 0; k < Stations; k++) {
				if (stationFlux[k] <= 0f) {
					continue;
				}
				var point = stationPoint[k];
				var fromNozzle = point - plume.Origin;
				var flow = fromNozzle.sqrMagnitude > 1e-6f ? fromNozzle.normalized : plume.Axis;

				// Cote expose : vers la tuyere, tire vers l'axe du jet quand le
				// point en est ecarte.
				var toward = -flow;
				var toAxis = plume.Origin +
					plume.Axis * Vector3.Dot(fromNozzle, plume.Axis) - point;
				if (toAxis.sqrMagnitude > 0.0025f) {
					toward = (toward + toAxis.normalized * TowardAxisWeight).normalized;
				}

				target.Deposit(ddose * stationFlux[k] / total, peak,
					toward, flow, point, stationSpot[k], false);
			}
		}

		/// <summary>
		/// Point ou l'axe du jet entre dans la piece cible. Faux si l'axe ne la
		/// rencontre pas sur la portee du jet (tuyere noyee dans la piece).
		/// </summary>
		private static bool AxisEntry(ref LosketPlumeModel.Plume plume, Part targetPart,
			out Vector3 entry)
		{
			entry = Vector3.zero;
			var nearest = float.MaxValue;
			var count = Physics.RaycastNonAlloc(plume.Origin, plume.Axis, hits,
				plume.Length, OcclusionMask, QueryTriggerInteraction.Ignore);
			for (var i = 0; i < count; i++) {
				var hit = hits[i];
				if (hit.collider == null || hit.distance >= nearest) {
					continue;
				}
				if (FlightGlobals.GetPartUpwardsCached(hit.collider.gameObject) != targetPart) {
					continue;
				}
				nearest = hit.distance;
				entry = hit.point;
			}
			return nearest < float.MaxValue;
		}

		/// <summary>
		/// Vrai si quelque chose s'interpose entre la tuyere et le point vise.
		/// La piece du moteur ne fait jamais ecran a son propre jet, et la
		/// cible ne se fait pas ecran a elle-meme.
		/// </summary>
		private static bool Occluded(Vector3 origin, Vector3 point, Part sourcePart, Part targetPart)
		{
			var delta = point - origin;
			var distance = delta.magnitude;
			if (distance < 0.05f) {
				return false;
			}
			var count = Physics.RaycastNonAlloc(origin, delta / distance, hits,
				distance + 0.1f, OcclusionMask, QueryTriggerInteraction.Ignore);
			for (var i = 0; i < count; i++) {
				var hit = hits[i];
				if (hit.collider == null || hit.distance >= distance - 0.1f) {
					continue;
				}
				var hitPart = FlightGlobals.GetPartUpwardsCached(hit.collider.gameObject);
				if (hitPart == null) {
					// Debris physiques (morceaux de coiffe, eclats) : trop
					// fugaces pour faire ecran. Le reste est du decor.
					if (hit.collider.GetComponentInParent<physicalObject>() != null) {
						continue;
					}
					return true;
				}
				if (hitPart == sourcePart || hitPart == targetPart) {
					continue;
				}
				return true;
			}
			return false;
		}
	}
}
