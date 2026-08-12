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

		private readonly List<Renderer> overlays = new List<Renderer>();
		private readonly List<Material> materials = new List<Material>();
		private readonly List<Material[]> materialSlots = new List<Material[]>();
		private readonly List<Bounds> meshBounds = new List<Bounds>();
		private readonly string ownerId;

		public int Count { get { return overlays.Count; } }

		public IList<Renderer> Overlays { get { return overlays; } }
		public IList<Material> Materials { get { return materials; } }

		private LosketOverlayRig(string ownerId)
		{
			this.ownerId = ownerId;
		}

		/// <summary>
		/// Cree les overlays d'une piece. Purge d'abord tout overlay existant :
		/// l'editeur clone des GameObjects vivants (symetrie, copie alt+clic) et
		/// ces orphelins resteraient pilotes par les materiaux d'une autre piece.
		/// </summary>
		public static LosketOverlayRig Create(Part part, Shader shader, string ownerId)
		{
			var rig = new LosketOverlayRig(ownerId);

			var stale = 0;
			foreach (var t in part.GetComponentsInChildren<Transform>(true)) {
				if (t != null && t.name == OverlayName) {
					UnityEngine.Object.Destroy(t.gameObject);
					stale++;
				}
			}

			foreach (var source in part.FindModelComponents<MeshRenderer>()) {
				// Le drapeau de mission a son propre systeme de decalque.
				if (source.name.IndexOf("flag", StringComparison.OrdinalIgnoreCase) >= 0) {
					continue;
				}
				if (source.name == OverlayName) {
					continue;
				}

				var filter = source.GetComponent<MeshFilter>();
				if (filter == null || filter.sharedMesh == null) {
					continue;
				}

				var go = new GameObject(OverlayName);
				go.transform.SetParent(source.transform, false);
				go.layer = source.gameObject.layer;

				go.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;

				var material = new Material(shader);
				if (LosketBootstrap.TemperLut != null) {
					material.SetTexture("_TemperLut", LosketBootstrap.TemperLut);
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

				// Auto-reparation : d'autres systemes de KSP (variantes,
				// highlighter, opacite de l'editeur) reassignent parfois les
				// materiaux des renderers qu'ils trouvent sous le modele. On
				// journalise l'usurpateur — piece a conviction — et on reprend
				// la main.
				var current = r.sharedMaterial;
				if (!ReferenceEquals(current, m)) {
					LosketBootstrap.LogWarning(ownerId + " : materiau de l'overlay " + i +
						" remplace par '" +
						(current != null ? current.name + "' (shader " + current.shader.name + ")" : "null'") +
						" — reassigne");
					r.sharedMaterials = materialSlots[i];
				}

				var objDir = r.transform.InverseTransformDirection(p.WorldFlowDir).normalized;
				var b = meshBounds[i];

				// Projection de l'AABB du maillage sur l'axe du flux, pour le
				// gradient positionnel (1 au bord au vent, 0 a l'oppose).
				var center = Vector3.Dot(b.center, objDir);
				var extent = Mathf.Abs(b.extents.x * objDir.x) +
				             Mathf.Abs(b.extents.y * objDir.y) +
				             Mathf.Abs(b.extents.z * objDir.z);

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
				m.SetFloat("_FlowMin", center - extent);
				m.SetFloat("_FlowRange", 2f * extent);
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

		private static Vector3 LongestAxis(Vector3 size)
		{
			if (size.y >= size.x && size.y >= size.z) {
				return Vector3.up;
			}
			return size.x >= size.z ? Vector3.right : Vector3.forward;
		}
	}
}
