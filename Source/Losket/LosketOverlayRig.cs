using System;
using System.Collections.Generic;
using UnityEngine;

namespace Losket
{
	/// <summary>Parametres visuels pousses vers le shader d'overlay.</summary>
	public struct BurnParams
	{
		/// <summary>Direction du flux en espace monde (pointe vers le cote brule).</summary>
		public Vector3 WorldFlowDir;

		public float BurnMag;
		public float PeakTemp;
		public float DirPower;
		public float Spread;
		public float Wrap;
		public float Sharpness;
		public float Streak;
		public float NoiseScale;
		public float Pattern;
		public float Bleach;

		/// <summary>Couleur du depot : suie sombre pour la brulure, couleur du
		/// sol pour la poussiere.</summary>
		public Color DepositColor;

		/// <summary>File de rendu du materiau, 0 = celle du shader. La poussiere
		/// se rend apres la brulure (elle se depose par-dessus).</summary>
		public int RenderQueue;

		/// <summary>
		/// Si vrai, le gradient le long du flux utilise une fenetre exprimee en
		/// PROJECTION MONDE ([WorldFlowMin, WorldFlowMin + WorldFlowRange] le
		/// long de WorldFlowDir) au lieu des bornes du maillage de chaque piece.
		/// C'est ce qui rend le degrade continu entre pieces empilees : chaque
		/// piece lit sa fraction de la meme rampe vaisseau, au lieu de repartir
		/// de zero a son propre bord.
		/// </summary>
		public bool UseWorldWindow;
		public float WorldFlowMin;
		public float WorldFlowRange;

		/// <summary>Valeurs par defaut raisonnables pour l'accumulation en vol.</summary>
		public static BurnParams Defaults()
		{
			return new BurnParams {
				WorldFlowDir = Vector3.down,
				BurnMag = 0f,
				PeakTemp = 0f,
				DirPower = 1.2f,
				Spread = 2f,
				Wrap = 1.4f,
				Sharpness = 1.6f,
				Streak = 1f,
				NoiseScale = 3f,
				Pattern = 0f,
				Bleach = 0f,
				DepositColor = new Color(0.06f, 0.055f, 0.05f),
				RenderQueue = 0,
			};
		}
	}

	/// <summary>
	/// Gere les overlays de brulure d'une piece : duplication des MeshRenderers
	/// (voie B), reassignation des materiaux si un systeme de KSP les remplace,
	/// et poussee des parametres vers le shader.
	/// </summary>
	public class LosketOverlayRig
	{
		public const string OverlayName = "losketBurnOverlay";
		public const string DustOverlayName = "losketDustOverlay";

		private readonly List<Renderer> overlays = new List<Renderer>();
		private readonly List<Renderer> sources = new List<Renderer>();
		private readonly List<Material> materials = new List<Material>();
		private readonly List<Material[]> materialSlots = new List<Material[]>();
		private readonly List<Bounds> meshBounds = new List<Bounds>();
		private readonly string ownerId;
		private readonly string overlayName;

		public int Count { get { return overlays.Count; } }

		public IList<Renderer> Overlays { get { return overlays; } }
		public IList<Material> Materials { get { return materials; } }

		private LosketOverlayRig(string ownerId, string overlayName)
		{
			this.ownerId = ownerId;
			this.overlayName = overlayName;
		}

		/// <summary>
		/// Cree les overlays d'une piece. Purge d'abord tout overlay du meme nom :
		/// l'editeur clone des GameObjects vivants (symetrie, copie alt+clic) et
		/// ces orphelins resteraient pilotes par les materiaux d'une autre piece.
		/// La purge est limitee a son propre nom pour que les rigs de brulure et
		/// de poussiere d'une meme piece cohabitent.
		/// </summary>
		public static LosketOverlayRig Create(Part part, Shader shader, string ownerId,
			string overlayName = OverlayName)
		{
			var rig = new LosketOverlayRig(ownerId, overlayName);

			var stale = 0;
			foreach (var t in part.GetComponentsInChildren<Transform>(true)) {
				if (t != null && t.name == overlayName) {
					UnityEngine.Object.Destroy(t.gameObject);
					stale++;
				}
			}

			foreach (var source in part.FindModelComponents<MeshRenderer>()) {
				// Le drapeau de mission a son propre systeme de decalque.
				if (source.name.IndexOf("flag", StringComparison.OrdinalIgnoreCase) >= 0) {
					continue;
				}
				// Ne jamais dupliquer un overlay Losket, le sien ou celui d'un
				// autre rig de la meme piece.
				if (source.name == OverlayName || source.name == DustOverlayName) {
					continue;
				}

				var filter = source.GetComponent<MeshFilter>();
				if (filter == null || filter.sharedMesh == null) {
					continue;
				}

				var go = new GameObject(overlayName);
				go.transform.SetParent(source.transform, false);
				go.layer = source.gameObject.layer;

				go.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;

				var material = new Material(shader);
				if (LosketBootstrap.TemperLut != null) {
					material.SetTexture("_TemperLut", LosketBootstrap.TemperLut);
				}

				// Pieces a texture ajouree (parachutes, poutrelles, grilles) : on
				// reprend l'alpha de la texture d'origine comme masque de
				// decoupe, mais UNIQUEMENT si le shader de base est de type
				// cutout/transparent. Sur les shaders opaques de KSP, l'alpha de
				// _MainTex encode la specularite : le prendre pour un masque
				// effacerait le depot sur les zones mates.
				var sourceMat = source.sharedMaterial;
				if (sourceMat != null && sourceMat.shader != null && sourceMat.mainTexture != null) {
					var shaderName = sourceMat.shader.name;
					if (shaderName.IndexOf("Cutoff", StringComparison.OrdinalIgnoreCase) >= 0 ||
					    shaderName.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0 ||
					    shaderName.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
					    shaderName.IndexOf("Translucent", StringComparison.OrdinalIgnoreCase) >= 0 ||
					    shaderName.IndexOf("Alpha", StringComparison.OrdinalIgnoreCase) >= 0) {
						material.SetTexture("_BaseTex", sourceMat.mainTexture);
						material.SetTextureScale("_BaseTex", sourceMat.mainTextureScale);
						material.SetTextureOffset("_BaseTex", sourceMat.mainTextureOffset);
						material.SetFloat("_UseBaseAlpha", 1f);
					}
				}

				var renderer = go.AddComponent<MeshRenderer>();
				// Un materiau par sous-maillage, sinon seuls les premiers sont couverts.
				var slots = new Material[filter.sharedMesh.subMeshCount];
				for (var i = 0; i < slots.Length; i++) {
					slots[i] = material;
				}
				renderer.sharedMaterials = slots;
				renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
				renderer.receiveShadows = false;

				rig.overlays.Add(renderer);
				rig.sources.Add(source);
				rig.materials.Add(material);
				rig.materialSlots.Add(slots);
				rig.meshBounds.Add(filter.sharedMesh.bounds);
			}

			LosketBootstrap.Log(ownerId + " : " + rig.overlays.Count + " overlay(s)" +
				(stale > 0 ? ", " + stale + " orphelin(s) purge(s)" : ""));
			return rig;
		}

		/// <summary>Pousse les parametres vers tous les overlays de la piece.</summary>
		public void Apply(BurnParams p)
		{
			for (var i = 0; i < materials.Count; i++) {
				var r = overlays[i];
				if (r == null) {
					continue;
				}
				var m = materials[i];

				// Suit la visibilite du renderer d'origine : une voile de
				// parachute repliee (renderer desactive) ne doit pas laisser un
				// fantome de poussiere flotter a sa place.
				var src = sources[i];
				var srcVisible = src != null && src.enabled;
				if (r.enabled != srcVisible) {
					r.enabled = srcVisible;
				}
				if (!srcVisible) {
					continue;
				}

				// Auto-reparation : d'autres systemes de KSP (variantes,
				// highlighter, opacite de l'editeur) reassignent parfois les
				// materiaux des renderers qu'ils trouvent sous le modele. On
				// journalise l'usurpateur - piece a conviction - et on reprend
				// la main.
				var current = r.sharedMaterial;
				if (!ReferenceEquals(current, m)) {
					LosketBootstrap.LogWarning(ownerId + " : materiau de l'overlay " + i +
						" remplace par '" +
						(current != null ? current.name + "' (shader " + current.shader.name + ")" : "null'") +
						" - reassigne");
					r.sharedMaterials = materialSlots[i];
				}

				var objDir = r.transform.InverseTransformDirection(p.WorldFlowDir).normalized;
				var b = meshBounds[i];

				// Fenetre du gradient positionnel. Deux modes :
				//  - fenetre vaisseau (continuite entre pieces) : la fenetre
				//    monde est convertie dans l'espace objet de cet overlay via
				//    dot(posMonde, dir) = s*dot(posObjet, dirObjet) + dot(t, dir) ;
				//  - fenetre maillage (previsualisation d'une piece isolee).
				float flowMin, flowRange;
				if (p.UseWorldWindow) {
					var s = r.transform.lossyScale.x;
					if (Mathf.Abs(s) < 1e-4f) {
						s = 1f;
					}
					var t = Vector3.Dot(r.transform.position, p.WorldFlowDir);
					flowMin = (p.WorldFlowMin - t) / s;
					flowRange = p.WorldFlowRange / s;
				} else {
					var center = Vector3.Dot(b.center, objDir);
					var extent = Mathf.Abs(b.extents.x * objDir.x) +
					             Mathf.Abs(b.extents.y * objDir.y) +
					             Mathf.Abs(b.extents.z * objDir.z);
					flowMin = center - extent;
					flowRange = 2f * extent;
				}

				// Axe de la colonne vertebrale : la plus grande dimension du
				// maillage, debarrassee de sa composante le long du flux. C'est
				// la ligne de stagnation d'un corps allonge frappe de biais. La
				// composante axiale du flux donne la derive des stries vers
				// l'aval. Si le flux est parallele a l'axe de coque (rentree
				// pointe en avant), l'axe de colonne est degenere : on prend une
				// perpendiculaire quelconque, stable.
				var hullAxis = LongestAxis(b.size);
				var axial = Vector3.Dot(objDir, hullAxis);
				var spine = hullAxis - objDir * axial;
				float slant;
				if (spine.sqrMagnitude < 1e-4f) {
					spine = Mathf.Abs(objDir.y) < 0.9f
						? Vector3.Cross(objDir, Vector3.up)
						: Vector3.Cross(objDir, Vector3.right);
					slant = 0f;
				} else {
					slant = axial * 0.5f;
				}
				spine.Normalize();

				m.SetVector("_BurnDirW", p.WorldFlowDir);
				m.SetVector("_BurnDirO", objDir);
				m.SetVector("_SpineAxisO", spine);
				m.SetFloat("_SlantAft", slant);
				m.SetFloat("_FlowMin", flowMin);
				m.SetFloat("_FlowRange", flowRange);
				m.SetFloat("_BurnMag", p.BurnMag);
				m.SetFloat("_PeakTemp", p.PeakTemp);
				m.SetFloat("_DirPower", p.DirPower);
				m.SetFloat("_Spread", p.Spread);
				m.SetFloat("_Wrap", p.Wrap);
				m.SetFloat("_Sharpness", p.Sharpness);
				m.SetFloat("_Streak", p.Streak);
				m.SetFloat("_NoiseScale", p.NoiseScale);
				m.SetFloat("_Pattern", p.Pattern);
				m.SetFloat("_Bleach", p.Bleach);
				m.SetColor("_SootColor", p.DepositColor);
				if (p.RenderQueue > 0 && m.renderQueue != p.RenderQueue) {
					m.renderQueue = p.RenderQueue;
				}
			}
		}

		public void Destroy()
		{
			foreach (var overlay in overlays) {
				if (overlay != null) {
					UnityEngine.Object.Destroy(overlay.gameObject);
				}
			}
			foreach (var material in materials) {
				if (material != null) {
					UnityEngine.Object.Destroy(material);
				}
			}
			overlays.Clear();
			materials.Clear();
			materialSlots.Clear();
			meshBounds.Clear();
		}

		/// <summary>
		/// Fenetre de projection de tout le vaisseau le long d'une direction
		/// monde : bornes des positions de pieces, elargies d'une marge pour
		/// couvrir leurs maillages. Sert au gradient continu entre pieces.
		/// </summary>
		public static void ComputeVesselWindow(Vessel vessel, Vector3 worldDir,
			out float min, out float range)
		{
			const float margin = 1.5f;
			var lo = float.MaxValue;
			var hi = float.MinValue;
			for (var i = 0; i < vessel.parts.Count; i++) {
				var p = vessel.parts[i];
				if (p == null) {
					continue;
				}
				var d = Vector3.Dot(p.transform.position, worldDir);
				if (d < lo) {
					lo = d;
				}
				if (d > hi) {
					hi = d;
				}
			}
			if (lo > hi) {
				lo = 0f;
				hi = 0f;
			}
			min = lo - margin;
			range = hi - lo + 2f * margin;
		}

		private static Vector3 LongestAxis(Vector3 size)
		{
			if (size.y >= size.x && size.y >= size.z) {
				return Vector3.up;
			}
			return size.x >= size.z ? Vector3.right : Vector3.forward;
		}
	}
}
