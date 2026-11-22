using UnityEngine;

namespace Losket
{
	/// <summary>
	/// Tirage de la tuile manquante d'ou part la coulure de rouille du motif
	/// Tuiles (type vol 15) : un point de la surface TUILEE de la piece, pris
	/// au hasard a proportion de la surface, puis cale sur la tuile de la
	/// texture qui le contient.
	///
	/// Le shader ne peut pas faire ce tirage : il ne connait la surface que par
	/// le pixel qu'il dessine. Sa premiere version tirait donc la source dans
	/// la texture et mesurait la coulure en UV ; sur un maillage decoupe en
	/// iles UV (le nez du Starship SEP : 146 iles, des anneaux d'un a trois
	/// metres) elle s'arretait a chaque bord d'ile. Ici la source est un point
	/// de l'espace, et la coulure se mesure dans l'espace.
	///
	/// Ne depend que d'UnityEngine : le meme fichier tourne dans le projet
	/// Unity des shaders pour verifier le tirage hors jeu.
	/// </summary>
	public static class LosketRustSource
	{
		/// <summary>Cote de la grille de densite de joints (carte de joints
		/// relue a gros grain, une valeur par bloc de texels).</summary>
		public const int GridSize = 32;

		/// <summary>Seuil au-dessus duquel un bloc de la grille est tuile. La
		/// carte vaut 0,045 sur les tuiles du Starship SEP (11 sur 255) et
		/// moins de 0,003 sur son inox (0 ou 1).</summary>
		public const byte TiledThreshold = 5;

		/// <summary>Part de surface tuilee qui perd une tuile, par unite du
		/// reglage "tuiles cassees" : au reglage par defaut (0,01) une piece
		/// de 100 m2 de tuiles a 40 % de chances de porter une coulure, soit
		/// a peu pres une coulure par Starship (nez 105 m2, ailerons quelques
		/// metres carres a l'echelle du jeu).</summary>
		public const float ChancePerArea = 0.4f;

		/// <summary>Cote de la fenetre de la carte de joints relue autour de
		/// la source, en texels : assez pour une tuile de cent texels.</summary>
		public const int WindowSize = 128;

		/// <summary>Valeur de la carte (sur 255) a partir de laquelle un texel
		/// de la fenetre est un joint. Le meme seuil sert au shader
		/// (TileHole) : 0,08. Les joints du Starship SEP y valent de 0,13
		/// (aretes peu contrastees) a 0,5.</summary>
		public const byte SeamTexel = 20;

		public struct Pick
		{
			/// <summary>Faux si la piece n'a aucun triangle tuile lisible.</summary>
			public bool Found;
			/// <summary>Rang du maillage qui porte la source (celui passe a AddMesh).</summary>
			public int Mesh;
			/// <summary>Position et normale, dans le repere objet de ce maillage.</summary>
			public Vector3 Position;
			public Vector3 Normal;
			/// <summary>UV de la source dans la texture telle que le materiau
			/// l'affiche (echelle et decalage compris), non repliees.</summary>
			public Vector2 Uv;
			/// <summary>Triangle elu : sommets (repere objet) et leurs UV, pour
			/// ramener un point de la texture dans l'espace.</summary>
			public Vector3 A, B, C;
			public Vector2 UvA, UvB, UvC;
			/// <summary>Metres (repere piece) par unite d'UV sur ce triangle.</summary>
			public float MetresPerUv;
		}

		/// <summary>Fenetre de la carte de joints autour d'un point : canal R,
		/// WindowSize au carre, ligne 0 en bas ; X0, Y0 = texel de la carte au
		/// coin bas-gauche (non replie).</summary>
		public struct Window
		{
			public byte[] Data;
			public int X0, Y0;
			public int MapWidth, MapHeight;
		}

		/// <summary>Vrai si la grille porte au moins un bloc tuile : evite de
		/// lire les sommets d'un maillage dont la texture n'a pas de tuiles.</summary>
		public static bool AnyTiled(byte[] grid)
		{
			if (grid == null) {
				return false;
			}
			for (var i = 0; i < grid.Length; i++) {
				if (grid[i] >= TiledThreshold) {
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Relit la carte de joints a gros grain : canal R, GridSize au carre,
		/// ligne 0 en bas. Le blit vers une petite cible fait lire au GPU le
		/// niveau de mip de la carte dont le texel vaut un bloc. A appeler
		/// juste apres la cuisson (une fois par texture) : ReadPixels attend
		/// le GPU.
		/// </summary>
		public static byte[] ReadGrid(RenderTexture seam)
		{
			return ReadRed(seam, GridSize, Vector2.one, Vector2.zero);
		}

		/// <summary>
		/// Relit la carte de joints texel pour texel (niveau 0) dans une
		/// fenetre centree sur un point de la texture. Une fois par tirage.
		/// </summary>
		public static Window ReadWindow(RenderTexture seam, Vector2 uv)
		{
			var w = new Window { MapWidth = seam.width, MapHeight = seam.height };
			w.X0 = Mathf.FloorToInt(uv.x * seam.width) - WindowSize / 2;
			w.Y0 = Mathf.FloorToInt(uv.y * seam.height) - WindowSize / 2;
			// Un pixel de la cible par texel de la carte, centres alignes.
			w.Data = ReadRed(seam, WindowSize,
				new Vector2(WindowSize / (float)seam.width, WindowSize / (float)seam.height),
				new Vector2(w.X0 / (float)seam.width, w.Y0 / (float)seam.height));
			return w;
		}

		private static byte[] ReadRed(RenderTexture source, int size, Vector2 scale, Vector2 offset)
		{
			var small = RenderTexture.GetTemporary(size, size, 0,
				RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
			var previous = RenderTexture.active;
			Graphics.Blit(source, small, scale, offset);
			RenderTexture.active = small;
			var tex = new Texture2D(size, size, TextureFormat.RGB24, false, true);
			tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
			var pixels = tex.GetPixels32();
			RenderTexture.active = previous;
			RenderTexture.ReleaseTemporary(small);
			Object.DestroyImmediate(tex);
			var data = new byte[size * size];
			for (var i = 0; i < data.Length && i < pixels.Length; i++) {
				data[i] = pixels[i].r;
			}
			return data;
		}

		/// <summary>Chances qu'une piece porte une tuile manquante.</summary>
		public static float Chance(float brokenFrac, float tiledArea)
		{
			return Mathf.Clamp01(brokenFrac * tiledArea * ChancePerArea);
		}

		/// <summary>
		/// Trouve la tuile qui contient un point de la fenetre : la cellule
		/// fermee par des joints autour de lui. Des rayons partent du point et
		/// s'arretent au premier joint ; les paires de rayons opposes
		/// recentrent le point jusqu'a ce qu'il ne bouge plus, et la portee moyenne des
		/// rayons qui butent donne le rayon de la tuile. Les rayons qui filent
		/// par un trou du trace (le detecteur ne rend pas toutes les aretes)
		/// sont ecartes : un remplissage par diffusion fuirait dans la tuile
		/// voisine.
		/// Faux s'il n'y a pas de cellule fermee de taille raisonnable autour
		/// du point (inox, panneau plus grand que la fenetre).
		/// </summary>
		/// <param name="x">Point de depart puis centre de la tuile, en texels
		/// de la fenetre.</param>
		/// <param name="radius">Rayon de la tuile en texels.</param>
		public static bool FindTile(byte[] window, ref float x, ref float y, out float radius)
		{
			radius = 0f;
			const int size = WindowSize;
			if (window == null || window.Length != size * size) {
				return false;
			}
			// Depart sur un joint ou contre lui : on glisse vers le texel le
			// plus franc a six texels a la ronde (un carrefour de trois joints
			// fait une tache de trois texels de rayon).
			int sx = Mathf.Clamp((int)x, 0, size - 1), sy = Mathf.Clamp((int)y, 0, size - 1);
			byte best = 255;
			var bestDist = int.MaxValue;
			int bx = sx, by = sy;
			for (var dy = -6; dy <= 6; dy++) {
				for (var dx = -6; dx <= 6; dx++) {
					int px = sx + dx, py = sy + dy;
					if (px < 0 || py < 0 || px >= size || py >= size) {
						continue;
					}
					var v = window[py * size + px];
					var d2 = dx * dx + dy * dy;
					if (v < best || (v == best && d2 < bestDist)) {
						best = v;
						bestDist = d2;
						bx = px;
						by = py;
					}
				}
			}
			if (best >= SeamTexel) {
				return false;
			}
			float cx = bx + 0.5f, cy = by + 0.5f;

			const int rays = 32;
			const int passes = 12;
			const float reach = size * 0.45f;
			var dist = new float[rays];
			var sorted = new float[rays];
			for (var pass = 0; pass < passes; pass++) {
				for (var r = 0; r < rays; r++) {
					var angle = r * (2f * Mathf.PI / rays);
					float ca = Mathf.Cos(angle), sa = Mathf.Sin(angle);
					var hit = reach;
					for (var t = 0.5f; t < reach; t += 0.5f) {
						int px = (int)(cx + ca * t), py = (int)(cy + sa * t);
						if (px < 0 || py < 0 || px >= size || py >= size) {
							break;
						}
						if (window[py * size + px] >= SeamTexel) {
							hit = t;
							break;
						}
					}
					dist[r] = hit;
					sorted[r] = hit;
				}
				System.Array.Sort(sorted);
				// Rayon interieur de la cellule : le quart bas des portees. La
				// mediane grossit des qu'une arete manque a la carte (le
				// detecteur ne rend pas toutes les aretes peu contrastees) ;
				// les rayons qui filent par ce trou sont ecartes.
				var inner = Mathf.Max(sorted[rays / 4], 1f);
				// Recentrage par paires de rayons opposes qui butent tous deux
				// sur la cellule : le centre est a mi-chemin de leurs impacts.
				// (La moyenne de u (u.e) sur les directions u vaut e/2 : on
				// double.) Tolerance large tant que le point est excentre.
				var loose = 1.7f * inner;
				float mx = 0f, my = 0f;
				var pairs = 0;
				for (var r = 0; r < rays / 2; r++) {
					float d1 = dist[r], d2 = dist[r + rays / 2];
					if (d1 > loose || d2 > loose) {
						continue;
					}
					var angle = r * (2f * Mathf.PI / rays);
					var half = 0.5f * (d1 - d2);
					mx += Mathf.Cos(angle) * half;
					my += Mathf.Sin(angle) * half;
					pairs++;
				}
				if (pairs >= 4) {
					mx *= 2f / pairs;
					my *= 2f / pairs;
				} else {
					// Trop peu de paires fermees : on s'ecarte du joint le
					// plus proche et on recommence.
					var nearest = 0;
					for (var r = 1; r < rays; r++) {
						if (dist[r] < dist[nearest]) {
							nearest = r;
						}
					}
					var away = nearest * (2f * Mathf.PI / rays) + Mathf.PI;
					mx = Mathf.Cos(away) * 3f;
					my = Mathf.Sin(away) * 3f;
				}
				var shift = Mathf.Sqrt(mx * mx + my * my);
				if (shift < 0.6f && pairs >= 4) {
					// Pose : cellule fermee aux deux tiers au moins, et aucun
					// rayon ne bute tout contre le centre.
					var limit = 1.35f * inner;
					var kept = 0;
					var sum = 0f;
					for (var r = 0; r < rays; r++) {
						if (dist[r] <= limit) {
							kept++;
							sum += dist[r];
						}
					}
					if (inner < 3f || inner >= reach * 0.6f || kept < rays * 2 / 3 ||
					    sorted[0] < 0.7f * inner) {
						return false;
					}
					x = cx;
					y = cy;
					radius = sum / kept;
					return true;
				}
				for (var k = 1f; k >= 0.25f; k *= 0.5f) {
					float nx = Mathf.Clamp(cx + k * mx, 8f, size - 8f);
					float ny = Mathf.Clamp(cy + k * my, 8f, size - 8f);
					if (window[(int)ny * size + (int)nx] < SeamTexel) {
						cx = nx;
						cy = ny;
						break;
					}
				}
			}
			return false;
		}

		/// <summary>
		/// Ramene un point de la texture (UV du materiau) dans l'espace, par
		/// la carte affine du triangle elu. Le point peut sortir un peu du
		/// triangle (une demi-tuile) : la surface est prise plane a cette
		/// echelle.
		/// </summary>
		public static Vector3 UvToObject(Pick p, Vector2 uv)
		{
			var e1 = p.UvB - p.UvA;
			var e2 = p.UvC - p.UvA;
			var det = e1.x * e2.y - e1.y * e2.x;
			if (Mathf.Abs(det) < 1e-12f) {
				return p.Position;
			}
			var q = uv - p.UvA;
			var s = (q.x * e2.y - q.y * e2.x) / det;
			var t = (e1.x * q.y - e1.y * q.x) / det;
			return p.A + (p.B - p.A) * s + (p.C - p.A) * t;
		}

		/// <summary>
		/// Tirage a reservoir pondere par la surface, sur les triangles tuiles
		/// de tous les maillages d'une piece presentes l'un apres l'autre. Le
		/// resultat ne depend que de la graine et des maillages : la meme piece
		/// retrouve sa tuile manquante a chaque chargement.
		/// </summary>
		public sealed class Sampler
		{
			private readonly System.Random rng;
			private double total;
			private Pick pick;

			public Sampler(float seed)
			{
				rng = new System.Random(unchecked((int)seed * 7919 + 17));
			}

			/// <summary>Surface tuilee vue jusqu'ici, en metres carres du
			/// repere de la piece.</summary>
			public float Area { get { return (float)total; } }

			public Pick Result { get { return pick; } }

			/// <summary>Tirage uniforme suivant de la meme suite (presence de
			/// la coulure, longueur).</summary>
			public float Next()
			{
				return (float)rng.NextDouble();
			}

			/// <param name="index">Rang du maillage, rendu dans Pick.Mesh.</param>
			/// <param name="toPart">Repere objet du maillage vers repere piece
			/// (sert a mesurer les surfaces en metres).</param>
			/// <param name="grid">Densite de joints a gros grain de la texture
			/// de base, GridSize au carre, ligne 0 en bas (v = 0).</param>
			/// <param name="uvScale">Echelle et decalage de la texture dans
			/// le materiau de la piece.</param>
			public void AddMesh(int index, Vector3[] vertices, Vector3[] normals, Vector2[] uv,
				int[] triangles, Matrix4x4 toPart, byte[] grid, Vector2 uvScale, Vector2 uvOffset)
			{
				if (vertices == null || uv == null || triangles == null || grid == null ||
				    uv.Length != vertices.Length || grid.Length != GridSize * GridSize) {
					return;
				}
				var hasNormals = normals != null && normals.Length == vertices.Length;
				for (var i = 0; i + 2 < triangles.Length; i += 3) {
					int ia = triangles[i], ib = triangles[i + 1], ic = triangles[i + 2];
					if (ia < 0 || ib < 0 || ic < 0 || ia >= vertices.Length ||
					    ib >= vertices.Length || ic >= vertices.Length) {
						continue;
					}
					var ua = Vector2.Scale(uv[ia], uvScale) + uvOffset;
					var ub = Vector2.Scale(uv[ib], uvScale) + uvOffset;
					var uc = Vector2.Scale(uv[ic], uvScale) + uvOffset;
					var centre = (ua + ub + uc) / 3f;
					if (!Tiled(grid, centre.x, centre.y)) {
						continue;
					}
					var a = vertices[ia];
					var b = vertices[ib];
					var c = vertices[ic];
					var cross = Vector3.Cross(toPart.MultiplyVector(b - a), toPart.MultiplyVector(c - a));
					var area = 0.5 * cross.magnitude;
					if (area <= 1e-9) {
						continue;
					}
					total += area;
					// Reservoir : ce triangle remplace l'elu avec une chance
					// egale a sa part de la surface vue jusqu'ici.
					if (rng.NextDouble() * total >= area) {
						continue;
					}
					// Point uniforme dans le triangle.
					var u = (float)rng.NextDouble();
					var v = (float)rng.NextDouble();
					if (u + v > 1f) {
						u = 1f - u;
						v = 1f - v;
					}
					var w = 1f - u - v;
					var geometric = Vector3.Cross(b - a, c - a).normalized;
					var normal = geometric;
					if (hasNormals) {
						var smooth = normals[ia] * w + normals[ib] * u + normals[ic] * v;
						if (smooth.sqrMagnitude > 1e-6f) {
							normal = smooth.normalized;
						}
					}
					var uvArea = 0.5f * Mathf.Abs((ub.x - ua.x) * (uc.y - ua.y) - (ub.y - ua.y) * (uc.x - ua.x));
					pick.Found = true;
					pick.Mesh = index;
					pick.Position = a * w + b * u + c * v;
					pick.Normal = normal;
					pick.Uv = ua * w + ub * u + uc * v;
					pick.A = a;
					pick.B = b;
					pick.C = c;
					pick.UvA = ua;
					pick.UvB = ub;
					pick.UvC = uc;
					pick.MetresPerUv = uvArea > 1e-12f ? Mathf.Sqrt((float)area / uvArea) : 0f;
				}
			}

			private static bool Tiled(byte[] grid, float u, float v)
			{
				u -= Mathf.Floor(u);
				v -= Mathf.Floor(v);
				var x = Mathf.Min(GridSize - 1, (int)(u * GridSize));
				var y = Mathf.Min(GridSize - 1, (int)(v * GridSize));
				return grid[y * GridSize + x] >= TiledThreshold;
			}
		}
	}
}
