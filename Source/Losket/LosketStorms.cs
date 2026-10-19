using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Pont, en lecture seule, vers les tempetes de poussiere de Volumetric
	/// Clouds (EVE-Redux raymarche de Blackrack). Aucune reference dure : tout
	/// passe par reflexion, et la moindre piece manquante coupe la fonction
	/// avec une seule ligne de log. Sans le mod, rien ne change.
	///
	/// Le mod calcule lui-meme, sur le CPU, la couverture d'une couche en un
	/// point du monde (CloudsRaymarchedVolume.SampleCoverage) : carte de
	/// couverture tournee avec les nuages, courbe de hauteur, fondu temporel.
	/// Il s'en sert pour ses particules, ses gouttes et son son d'ambiance ; on
	/// lit la meme valeur. Le bruit de detail n'existe que sur le GPU : la
	/// valeur lue est la forme de la tempete a grande echelle.
	///
	/// Verifie sur EVE-Redux raymarche 2.2.1. Une couche hors de sa fenetre
	/// temporelle est desactivee (enabled = false) mais garde ses derniers
	/// fondus a 1 : sans tester enabled, on croirait la tempete permanente.
	/// Meme chose quand la camera s'eloigne trop : la couche n'est plus
	/// suivie, donc ignoree.
	/// </summary>
	public static class LosketStorms
	{
		// --- Reglages (LOSKET_SETTINGS, voir Configs/losket-settings.cfg) ---

		/// <summary>Faux : les tempetes ne salissent rien.</summary>
		public static bool Enabled = true;

		/// <summary>Une couche compte comme tempete si son nom contient ce
		/// mot (sans casse). Les couches de Duna du pack StockVolumetricClouds
		/// s'appellent Duna-dust-scattered et Duna-duststorm-big ; les cirrus
		/// de Duna n'ont pas "dust" dans leur nom.</summary>
		public static string LayerKeyword = "dust";

		/// <summary>Couverture sous laquelle on est hors tempete (meme seuil
		/// que les particules de poussiere du pack).</summary>
		public static float CoverageMin = 0.2f;

		/// <summary>Couverture a partir de laquelle la tempete est pleine.</summary>
		public static float CoverageMax = 0.75f;

		/// <summary>Temps (s de jeu) d'exposition a une tempete pleine qui
		/// couvre une piece a ~63 %.</summary>
		public static float DustTime = 7200f;

		/// <summary>Poids par couche (noeuds LOSKET_STORM_LAYER), 1 sans
		/// entree. Les voiles de poussiere diffuse de Duna sont tres fins a
		/// cote des grandes tempetes : ils salissent moins.</summary>
		private static readonly System.Collections.Generic.Dictionary<string, float> layerWeights =
			new System.Collections.Generic.Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

		// --- Etat de la reflexion ---

		private enum State { Unknown, Ready, Off }

		private static State state = State.Unknown;
		private static MethodInfo getObjectList;
		private static PropertyInfo nameProp;
		private static PropertyInfo bodyProp;
		private static PropertyInfo volumeProp;
		private static MethodInfo sampleCoverage;
		private static PropertyInfo volumeEnabled;
		private static readonly object[] sampleArgs = new object[3];

		/// <summary>Lit les reglages des tempetes dans un noeud LOSKET_SETTINGS.</summary>
		internal static void LoadConfig(ConfigNode node)
		{
			node.TryGetValue("stormDust", ref Enabled);
			var keyword = node.GetValue("stormLayerKeyword");
			if (!string.IsNullOrEmpty(keyword)) {
				LayerKeyword = keyword;
			}
			node.TryGetValue("stormCoverageMin", ref CoverageMin);
			node.TryGetValue("stormCoverageMax", ref CoverageMax);
			node.TryGetValue("stormDustTime", ref DustTime);
			DustTime = Mathf.Max(DustTime, 1f);
		}

		/// <summary>Lit les poids par couche (noeuds LOSKET_STORM_LAYER,
		/// cles name et weight). L'ancien format sur une ligne est tolere,
		/// voir LosketBootstrap.SplitLegacyPair.</summary>
		internal static void LoadLayers()
		{
			layerWeights.Clear();
			foreach (var node in GameDatabase.Instance.GetConfigNodes("LOSKET_STORM_LAYER")) {
				var name = node.GetValue("name");
				var weight = 1f;
				var hasWeight = node.TryGetValue("weight", ref weight);
				string legacyName, legacyWeight;
				if (!hasWeight && !node.HasValue("weight") &&
				    LosketBootstrap.SplitLegacyPair(name, "weight", out legacyName, out legacyWeight)) {
					name = legacyName;
					hasWeight = float.TryParse(legacyWeight, NumberStyles.Float,
						CultureInfo.InvariantCulture, out weight);
					LosketBootstrap.Log("noeud LOSKET_STORM_LAYER a l'ancien format (une ligne) reconnu : " + name);
				}
				if (string.IsNullOrEmpty(name) || !hasWeight) {
					LosketBootstrap.LogWarning("noeud LOSKET_STORM_LAYER incomplet ignore (name=" + name + ")");
					continue;
				}
				layerWeights[name] = Mathf.Max(0f, weight);
			}
			if (layerWeights.Count > 0) {
				LosketBootstrap.Log("tempetes : " + layerWeights.Count + " poids de couche(s)");
			}
		}

		private static float LayerWeight(string name)
		{
			float weight;
			return name != null && layerWeights.TryGetValue(name, out weight) ? weight : 1f;
		}

		/// <summary>
		/// Intensite de tempete de poussiere (0..1) au point donne, maximum sur
		/// les couches de tempete de l'astre. 0 sans Volumetric Clouds, hors
		/// tempete, ou si la reflexion a echoue.
		/// </summary>
		public static float Intensity(CelestialBody body, Vector3 worldPos)
		{
			if (!Enabled || body == null || !Resolve()) {
				return 0f;
			}
			try {
				var objects = getObjectList.Invoke(null, null) as IEnumerable;
				if (objects == null) {
					return 0f;
				}
				var best = 0f;
				foreach (var obj in objects) {
					if (obj == null || !IsStormLayer(obj, body)) {
						continue;
					}
					var weight = LayerWeight(nameProp.GetValue(obj, null) as string);
					if (weight <= 0f) {
						continue;
					}
					var volume = volumeProp.GetValue(obj, null);
					if (volume == null || !(bool)volumeEnabled.GetValue(volume, null)) {
						continue;
					}
					sampleArgs[0] = worldPos;
					sampleArgs[1] = 0f;
					sampleArgs[2] = false;      // pas de test de rayon : on est au sol
					var coverage = (float)sampleCoverage.Invoke(volume, sampleArgs);
					var intensity = weight * Mathf.Clamp01((coverage - CoverageMin) /
						Mathf.Max(CoverageMax - CoverageMin, 1e-3f));
					if (intensity > best) {
						best = intensity;
					}
				}
				return best;
			} catch (Exception e) {
				Disable("lecture des tempetes impossible (" + e.GetType().Name + " : " +
				        (e.InnerException ?? e).Message + ")");
				return 0f;
			}
		}

		private static bool IsStormLayer(object obj, CelestialBody body)
		{
			var bodyName = bodyProp.GetValue(obj, null) as string;
			if (bodyName != body.bodyName) {
				return false;
			}
			var name = nameProp.GetValue(obj, null) as string;
			return name != null &&
			       name.IndexOf(LayerKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		/// <summary>Cherche une fois les types et membres de Volumetric
		/// Clouds. Vrai si tout est la.</summary>
		private static bool Resolve()
		{
			if (state != State.Unknown) {
				return state == State.Ready;
			}
			state = State.Off;

			Type cloudsManager = null;
			Type cloudsObject = null;
			foreach (var loaded in AssemblyLoader.loadedAssemblies) {
				if (loaded.assembly == null) {
					continue;
				}
				cloudsManager = loaded.assembly.GetType("Atmosphere.CloudsManager", false);
				if (cloudsManager != null) {
					cloudsObject = loaded.assembly.GetType("Atmosphere.CloudsObject", false);
					break;
				}
			}
			if (cloudsManager == null || cloudsObject == null) {
				LosketBootstrap.Log("tempetes : EVE absent, fonction inactive");
				return false;
			}

			// GetObjectList est une methode statique de la base generique
			// GenericEVEManager<CloudsObject> : on remonte la hierarchie.
			for (var t = cloudsManager; t != null && getObjectList == null; t = t.BaseType) {
				getObjectList = t.GetMethod("GetObjectList",
					BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
					null, Type.EmptyTypes, null);
			}
			nameProp = cloudsObject.GetProperty("Name");
			bodyProp = cloudsObject.GetProperty("Body");
			volumeProp = cloudsObject.GetProperty("LayerRaymarchedVolume");
			if (getObjectList == null || nameProp == null || bodyProp == null) {
				Disable("EVE present mais sa structure a change (liste des couches introuvable)");
				return false;
			}
			if (volumeProp == null) {
				LosketBootstrap.Log("tempetes : EVE sans nuages volumetriques, fonction inactive");
				return false;
			}

			var volumeType = volumeProp.PropertyType;
			sampleCoverage = volumeType.GetMethod("SampleCoverage",
				BindingFlags.Public | BindingFlags.Instance, null,
				new[] { typeof(Vector3), typeof(float).MakeByRefType(), typeof(bool) }, null);
			volumeEnabled = volumeType.GetProperty("enabled",
				BindingFlags.Public | BindingFlags.Instance);
			if (sampleCoverage == null || sampleCoverage.ReturnType != typeof(float) ||
			    volumeEnabled == null || volumeEnabled.PropertyType != typeof(bool)) {
				Disable("Volumetric Clouds present mais SampleCoverage ou enabled introuvable");
				return false;
			}

			state = State.Ready;
			LosketBootstrap.Log("tempetes : Volumetric Clouds detecte (" +
			                    cloudsManager.Assembly.GetName().Version + "), couches dont le nom contient \"" +
			                    LayerKeyword + "\"");
			return true;
		}

		private static void Disable(string reason)
		{
			state = State.Off;
			LosketBootstrap.LogWarning("tempetes : " + reason + " - fonction coupee");
		}
	}
}
