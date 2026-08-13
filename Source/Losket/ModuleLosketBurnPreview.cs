using System.Collections.Generic;
using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Module de PREVISUALISATION du shader de brulure : expose les parametres du
	/// shader dans le menu clic-droit pour regler le rendu a l'oeil, sans aucune
	/// simulation. La gestion des overlays vit dans LosketOverlayRig, partagee
	/// avec le module d'accumulation en vol.
	/// </summary>
	public class ModuleLosketBurnPreview : PartModule
	{
		private const string Group = "LosketPreview";
		private const string GroupTitle = "Losket - brulure (dev)";

		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true,
			guiName = "Brulure", groupName = Group, groupDisplayName = GroupTitle),
		 UI_FloatRange(minValue = 0f, maxValue = 1f, stepIncrement = 0.01f)]
		public float burnMag = 0.5f;

		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true,
			guiName = "Temperature (revenu)", groupName = Group, groupDisplayName = GroupTitle),
		 UI_FloatRange(minValue = 0f, maxValue = 1f, stepIncrement = 0.01f)]
		public float peakTemp = 0.65f;

		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true,
			guiName = "Cote brule", groupName = Group, groupDisplayName = GroupTitle),
		 UI_ChooseOption(options = new[] {
			"Bas", "Haut", "Avant", "Arriere", "Gauche", "Droite",
			"Avant-Bas", "Avant-Haut" })]
		public string dirPreset = "Bas";

		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true,
			guiName = "Motif", groupName = Group, groupDisplayName = GroupTitle),
		 UI_ChooseOption(options = new[] { "Taches", "Stries" })]
		public string patternPreset = "Taches";

		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true,
			guiName = "Concentration", groupName = Group, groupDisplayName = GroupTitle),
		 UI_FloatRange(minValue = 0.2f, maxValue = 8f, stepIncrement = 0.1f)]
		public float dirPower = 2f;

		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true,
			guiName = "Etalement", groupName = Group, groupDisplayName = GroupTitle),
		 UI_FloatRange(minValue = 0.2f, maxValue = 8f, stepIncrement = 0.1f)]
		public float spread = 1.5f;

		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true,
			guiName = "Enveloppement", groupName = Group, groupDisplayName = GroupTitle),
		 UI_FloatRange(minValue = 0f, maxValue = 2f, stepIncrement = 0.05f)]
		public float wrap = 1.2f;

		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true,
			guiName = "Nettete", groupName = Group, groupDisplayName = GroupTitle),
		 UI_FloatRange(minValue = 0.5f, maxValue = 8f, stepIncrement = 0.1f)]
		public float sharpness = 2f;

		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true,
			guiName = "Stries", groupName = Group, groupDisplayName = GroupTitle),
		 UI_FloatRange(minValue = 1f, maxValue = 16f, stepIncrement = 0.5f)]
		public float streak = 1f;

		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true,
			guiName = "Echelle du bruit", groupName = Group, groupDisplayName = GroupTitle),
		 UI_FloatRange(minValue = 0.5f, maxValue = 16f, stepIncrement = 0.5f)]
		public float noiseScale = 4f;

		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true,
			guiName = "Eclaircir", groupName = Group, groupDisplayName = GroupTitle),
		 UI_FloatRange(minValue = 0f, maxValue = 1f, stepIncrement = 0.01f)]
		public float bleach = 0f;

		private static readonly Dictionary<string, Vector3> Presets =
			new Dictionary<string, Vector3> {
				{ "Bas", Vector3.down },
				{ "Haut", Vector3.up },
				{ "Avant", Vector3.forward },
				{ "Arriere", Vector3.back },
				{ "Gauche", Vector3.left },
				{ "Droite", Vector3.right },
				// Flux diagonal : rentree a forte incidence type Starship, ou les
				// stries derivent vers l'arriere le long de la coque.
				{ "Avant-Bas", new Vector3(0f, -0.45f, 1f) },
				{ "Avant-Haut", new Vector3(0f, 0.45f, 1f) },
			};

		private LosketOverlayRig rig;

		private string OwnerId
		{
			get { return part.partInfo.name + "#" + GetInstanceID(); }
		}

		public override void OnStart(StartState state)
		{
			// Outil de developpement : masque, sauf si l'option "Interface de
			// developpement" des reglages de partie (menu Difficulte > Losket)
			// est activee. L'interface normale vit dans ModuleLosketBurn.
			var settings = LosketSettings.Instance;
			if (settings == null || !settings.interfaceDev) {
				foreach (BaseField field in Fields) {
					field.guiActive = false;
					field.guiActiveEditor = false;
				}
				Events["DumpState"].guiActive = false;
				Events["DumpState"].guiActiveEditor = false;
				return;
			}

			// En vol, si la piece porte le module d'accumulation, ce module-ci
			// devient un simple porteur de style : c'est la physique qui decide
			// de l'intensite, pas les curseurs. Un vaisseau lance propre reste
			// propre jusqu'a sa rentree.
			if (HighLogic.LoadedSceneIsFlight &&
			    part.FindModuleImplementing<ModuleLosketBurn>() != null) {
				return;
			}

			if (!LosketBootstrap.ShadersLoaded) {
				LosketBootstrap.LogWarning(part.partInfo.name +
					" : shaders non charges, previsualisation desactivee");
				return;
			}

			var shader = LosketBootstrap.GetShader("Losket/BurnOverlay");
			if (shader == null) {
				LosketBootstrap.LogError("shader Losket/BurnOverlay absent du bundle");
				return;
			}

			rig = LosketOverlayRig.Create(part, shader, OwnerId);
		}

		private void LateUpdate()
		{
			if (rig == null || rig.Count == 0) {
				return;
			}

			Vector3 local;
			if (!Presets.TryGetValue(dirPreset, out local)) {
				local = Vector3.down;
			}

			var p = BurnParams.Defaults();
			p.WorldFlowDir = part.transform.TransformDirection(local.normalized);
			p.BurnMag = burnMag;
			p.PeakTemp = peakTemp;
			p.DirPower = dirPower;
			p.Spread = spread;
			p.Wrap = wrap;
			p.Sharpness = sharpness;
			p.Streak = streak;
			p.NoiseScale = noiseScale;
			p.Pattern = patternPreset == "Stries" ? 1f : 0f;
			p.Bleach = bleach;

			rig.Apply(p);
		}

		/// <summary>
		/// Photographie l'etat de la chaine curseur -> materiau -> rendu, et
		/// balaie la scene a la recherche d'overlays orphelins.
		/// </summary>
		[KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "Journaliser l'etat (Losket)",
			groupName = Group, groupDisplayName = GroupTitle)]
		public void DumpState()
		{
			LosketBootstrap.Log("=== DumpState " + OwnerId + " ===");
			LosketBootstrap.Log("  module.enabled=" + enabled +
				" isEnabled=" + isEnabled +
				" gameObject.activeInHierarchy=" + gameObject.activeInHierarchy);
			LosketBootstrap.Log("  burnMag=" + burnMag + " dirPreset=" + dirPreset +
				" motif=" + patternPreset + " overlays=" + (rig != null ? rig.Count : 0));

			if (rig != null) {
				for (var i = 0; i < rig.Count; i++) {
					var r = rig.Overlays[i];
					if (r == null) {
						LosketBootstrap.Log("  overlay[" + i + "] DETRUIT");
						continue;
					}
					var shared = r.sharedMaterial;
					LosketBootstrap.Log("  overlay[" + i + "] actif=" + r.gameObject.activeInHierarchy +
						" visible=" + r.isVisible +
						" _BurnMag(notre mat)=" + rig.Materials[i].GetFloat("_BurnMag").ToString("0.00") +
						" | mat du renderer='" + (shared != null ? shared.name : "null") +
						"' identique=" + ReferenceEquals(shared, rig.Materials[i]));
				}
			}

			var found = 0;
			foreach (var r in FindObjectsOfType<MeshRenderer>()) {
				if (r == null || r.name != LosketOverlayRig.OverlayName) {
					continue;
				}
				found++;
				var owner = r.GetComponentInParent<Part>();
				var mine = rig != null && rig.Overlays.Contains(r);
				var mat = r.sharedMaterial;
				LosketBootstrap.Log("  scene: overlay sous '" +
					(owner != null ? owner.partInfo.name + "#" + owner.GetInstanceID() : "AUCUNE PIECE") +
					"' pilote-par-ce-module=" + mine +
					" _BurnMag=" + (mat != null && mat.HasProperty("_BurnMag")
						? mat.GetFloat("_BurnMag").ToString("0.00") : "n/a"));
			}
			LosketBootstrap.Log("  scene: " + found + " overlay(s) Losket au total");
		}

		private void OnDestroy()
		{
			if (rig != null) {
				rig.Destroy();
				rig = null;
			}
		}
	}
}
