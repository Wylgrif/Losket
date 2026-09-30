using System.Collections.Generic;
using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Dit si une piece deployable est actuellement repliee. Ne concerne que
	/// les pieces dont KSP lui-meme declare l'etat de deploiement : panneaux
	/// solaires, antennes et radiateurs (ModuleDeployablePart) et parachutes.
	/// Lecture seule.
	/// </summary>
	public class LosketStowage
	{
		private readonly List<ModuleDeployablePart> deployables = new List<ModuleDeployablePart>();
		private readonly List<ModuleParachute> parachutes = new List<ModuleParachute>();

		/// <summary>Null si la piece ne porte aucun module de deploiement :
		/// rien a suivre, comportement inchange.</summary>
		public static LosketStowage For(Part part)
		{
			LosketStowage stowage = null;
			for (var i = 0; i < part.Modules.Count; i++) {
				var module = part.Modules[i];
				// ModuleDeployableSolarPanel, -Antenna et -Radiator en derivent.
				var deployable = module as ModuleDeployablePart;
				var parachute = module as ModuleParachute;
				if (deployable == null && parachute == null) {
					continue;
				}
				if (stowage == null) {
					stowage = new LosketStowage();
				}
				if (deployable != null) {
					stowage.deployables.Add(deployable);
				} else {
					stowage.parachutes.Add(parachute);
				}
			}
			return stowage;
		}

		/// <summary>Vrai si TOUT ce qui se deploie sur la piece est replie.</summary>
		public bool IsStowed
		{
			get
			{
				for (var i = 0; i < deployables.Count; i++) {
					var d = deployables[i];
					if (d == null) {
						continue;
					}
					// Sans animation de deploiement, rien ne se replie : un
					// panneau fixe qui suit le soleil balaierait hors de sa
					// boite. On ne decoupe pas.
					if (!d.useAnimation ||
					    d.deployState != ModuleDeployablePart.DeployState.RETRACTED) {
						return false;
					}
				}
				for (var i = 0; i < parachutes.Count; i++) {
					var p = parachutes[i];
					if (p == null) {
						continue;
					}
					// ACTIVE = arme mais encore dans son sac ; CUT = voile partie.
					var s = p.deploymentState;
					if (s == ModuleParachute.deploymentStates.SEMIDEPLOYED ||
					    s == ModuleParachute.deploymentStates.DEPLOYED) {
						return false;
					}
				}
				return true;
			}
		}
	}

	/// <summary>
	/// Enveloppe de depot : la boite qui englobait la geometrie d'une piece
	/// deployable au moment ou elle s'est marquee REPLIEE. Au rendu, ce qui en
	/// sort reste propre - un panneau solaire range dans son boitier pendant
	/// la rentree ne ressort pas brule (issue #4).
	///
	/// La boite est alignee sur les axes de la piece et stockee dans son
	/// repere : elle la suit dans tous ses mouvements, et seule sa taille
	/// s'ajuste. Une par piece et par type de marque.
	///
	/// Trois etats, persistants :
	///  - None   : jamais suivie (piece non deployable, ou sauvegarde
	///             anterieure) - aucun decoupage ;
	///  - Stowed : tout le depot a ete recu repliee - la boite decoupe ;
	///  - Open   : au moins une part du depot a ete recue deployee - plus de
	///             decoupage, jusqu'au nettoyage. Un panneau deploye qui suit
	///             le soleil balaie un volume sans rapport avec sa boite d'un
	///             instant : on ne decoupe que ce dont on est sur.
	///
	/// Volontairement limitee aux pieces deployables. Appliquee a toutes les
	/// pieces, elle effacerait les marques de tout ce qui bouge apres avoir
	/// ete expose : gouvernes braquees, portes de soute, tuyeres orientables.
	/// </summary>
	public static class LosketEnvelope
	{
		public const int None = 0;
		public const int Stowed = 1;
		public const int Open = 2;

		/// <summary>Depots entre deux mesures de la boite, tant que la piece
		/// reste repliee (sa geometrie ne bouge pas).</summary>
		private const int RefreshEvery = 100;

		/// <summary>
		/// A appeler a chaque depot. Le test d'etat est quasi gratuit ; la
		/// mesure de la boite n'a lieu qu'au premier depot puis de loin en loin.
		/// </summary>
		public static void Track(Part part, LosketStowage stowage, ref int state,
			ref Vector3 min, ref Vector3 max, ref int tick)
		{
			if (stowage == null || state == Open) {
				return;
			}
			if (!stowage.IsStowed) {
				state = Open;
				return;
			}
			if (state == Stowed && ++tick < RefreshEvery) {
				return;
			}
			tick = 0;

			Vector3 lo, hi;
			if (!LosketOverlayRig.MeasurePartBounds(part, out lo, out hi)) {
				return;
			}
			if (state == Stowed) {
				lo = Vector3.Min(lo, min);
				hi = Vector3.Max(hi, max);
			}
			min = lo;
			max = hi;
			state = Stowed;
		}
	}
}
