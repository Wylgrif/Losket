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
		/// Repere "motif" de la piece : transformation piece -> repere du
		/// vaisseau capture au premier vol (identite dans l'editeur). Le bruit y
		/// est echantillonne, ce qui le rend continu entre pieces voisines et
		/// insensible au vol, au staging et au docking.
		/// </summary>
		public Matrix4x4 PartToPattern;

		/// <summary>
		/// Fenetre du gradient d'etalement en espace motif, a l'echelle du
		/// vaisseau, calculee et figee par le module pendant l'accumulation.
		/// Si UsePatternWindow est faux, repli sur les bornes du maillage
		/// (previsualisation, anciennes sauvegardes jamais rebrulees).
		/// </summary>
		public bool UsePatternWindow;
		public float PatternWindowMin;
		public float PatternWindowRange;

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
				PartToPattern = Matrix4x4.identity,
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
		private Transform partTransform;

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
			rig.partTransform = part.transform;

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
				// Effets lumineux : flares de lampes, flammes et lueurs de
				// moteurs. Ce sont des maillages comme les autres sous le modele,
				// mais rien ne se depose sur de la lumiere. Reconnus par leur
				// shader (additif / particules / non eclaire) ou leur nom.
				if (IsLightEffect(source)) {
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

				var b = meshBounds[i];

				// Tout le motif (bruit, gradient, carte des stries) est calcule
				// dans l'espace "motif" : le repere du vaisseau capture au
				// premier vol de la piece (p.PartToPattern, identite dans
				// l'editeur). C'est ce qui rend le motif continu entre pieces
				// voisines, fige quel que soit le vol, et sans surprise au
				// docking : chaque vaisseau conserve le repere qu'il a capture,
				// la seule couture est au port d'amarrage.
				var objToPattern = p.PartToPattern *
					partTransform.worldToLocalMatrix * r.transform.localToWorldMatrix;
				var dirPattern = p.PartToPattern.MultiplyVector(
					partTransform.InverseTransformDirection(p.WorldFlowDir)).normalized;

				// Fenetre du gradient positionnel : celle du vaisseau entier si
				// le module l'a figee (continuite aux joints), sinon l'AABB du
				// maillage projete sur l'axe du flux, en espace motif.
				float flowMin, flowRange;
				if (p.UsePatternWindow) {
					flowMin = p.PatternWindowMin;
					flowRange = p.PatternWindowRange;
				} else {
					var centerP = objToPattern.MultiplyPoint3x4(b.center);
					var extentP =
						Mathf.Abs(Vector3.Dot(dirPattern, objToPattern.MultiplyVector(new Vector3(b.extents.x, 0f, 0f)))) +
						Mathf.Abs(Vector3.Dot(dirPattern, objToPattern.MultiplyVector(new Vector3(0f, b.extents.y, 0f)))) +
						Mathf.Abs(Vector3.Dot(dirPattern, objToPattern.MultiplyVector(new Vector3(0f, 0f, b.extents.z))));
					flowMin = Vector3.Dot(centerP, dirPattern) - extentP;
					flowRange = 2f * extentP;
				}

				// Axe de la colonne vertebrale : la plus grande dimension du
				// maillage, portee en espace motif et debarrassee de sa
				// composante le long du flux. C'est la ligne de stagnation d'un
				// corps allonge frappe de biais ; la composante axiale du flux
				// donne la derive des stries vers l'aval. Si le flux est
				// parallele a l'axe de coque, on prend une perpendiculaire
				// quelconque, stable.
				var hullAxis = objToPattern.MultiplyVector(LongestAxis(b.size)).normalized;
				var axial = Vector3.Dot(dirPattern, hullAxis);
				var spine = hullAxis - dirPattern * axial;
				float slant;
				if (spine.sqrMagnitude < 1e-4f) {
					spine = Mathf.Abs(dirPattern.y) < 0.9f
						? Vector3.Cross(dirPattern, Vector3.up)
						: Vector3.Cross(dirPattern, Vector3.right);
					slant = 0f;
				} else {
					slant = axial * 0.5f;
				}
				spine.Normalize();

				m.SetMatrix("_ObjToPattern", objToPattern);
				m.SetVector("_BurnDirW", p.WorldFlowDir);
				m.SetVector("_BurnDirO", dirPattern);
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

		private static bool IsLightEffect(Renderer source)
		{
			var name = source.name;
			if (name.IndexOf("flare", StringComparison.OrdinalIgnoreCase) >= 0 ||
			    name.IndexOf("flame", StringComparison.OrdinalIgnoreCase) >= 0 ||
			    name.IndexOf("plume", StringComparison.OrdinalIgnoreCase) >= 0 ||
			    name.IndexOf("glow", StringComparison.OrdinalIgnoreCase) >= 0) {
				return true;
			}

			var material = source.sharedMaterial;
			if (material == null || material.shader == null) {
				return false;
			}
			var shaderName = material.shader.name;
			return shaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
			       shaderName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0 ||
			       shaderName.IndexOf("Unlit", StringComparison.OrdinalIgnoreCase) >= 0 ||
			       shaderName.IndexOf("Distortion", StringComparison.OrdinalIgnoreCase) >= 0;
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
