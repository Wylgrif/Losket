// Carte de joints du motif Tuiles : detecteur de LIGNES sur la texture de base
// d'une piece, cuit UNE fois par texture dans une RenderTexture avec mipmaps
// (LosketOverlayRig.SeamMapFor, deux Graphics.Blit). Les interstices entre
// tuiles ressortent, qu'ils soient peints en clair ou en sombre et quel que
// soit leur contraste ; une texture d'inox nu donne une carte presque vide,
// donc pas de tuiles sur l'inox, sans aucun reglage.
//
// Le contraste se mesure en RAPPORT de luminance (log-luminance), pas en
// ecart absolu. Le Starship SEP porte trois peintures de tuiles pour une
// meme grille d'hexagones de 51 texels : joints clairs (Block 3 : 0,45 sur
// 0,12), joints tres clairs (Block 2 : 0,95 sur 0,22) et joints SOMBRES
// (Block 1 : 0,035 sur 0,12). En ecart absolu le Block 1 pese cinq fois moins
// que le Block 3 et dix fois moins que le Block 2 ; en rapport, les trois
// valent un facteur 3 a 4. La premiere version seuillait un ecart absolu lu
// au mip 1 de la texture (la cible fait la moitie de la source, le GPU
// choisissait donc ce niveau) : un joint d'un texel y est a moitie efface.
// Mesure sur SEP, elle ne relevait rien sur le Block 1, des troncons
// verticaux sur le Block 3 et tout le reseau, en trait gras, sur le Block 2.
//
// Passe 0, pre-passe : pour chaque texel de la cible, les QUATRE texels de la
// texture qu'il recouvre, lus un par un au niveau de mip le plus fin
// (_SeamSrcLod, _SeamSrcTexel), en log-luminance. Deux resumes du bloc :
//   R = leur moyenne (moyenne geometrique des luminances) : un joint clair
//       anticrenele y reste un trait fin et regulier ;
//   G = leur minimum (le texel le plus sombre) : un joint sombre d'un texel
//       y garde tout son contraste, la ou une moyenne le dilue de moitie.
// Les valeurs sont ramenees dans 0..1 (LOSKET_LOG_MIN, LOSKET_LOG_RANGE) pour
// tenir aussi dans une cible 8 bits si les demi-flottants manquent.
//
// Passe 1, detecteur : dans douze directions (les joints d'hexagones sont a
// 60 degres), la reponse en travers s = c - (a + b) / 2, a et b a
// _SeamRadius texels d'origine de part et d'autre, SIGNEE :
//   - crete (joint clair) : s > 0 dans le champ R ;
//   - vallee (joint sombre) : s < 0 dans le champ G.
// De chaque cote d'une ligne, la meme formule donne un lobe de signe oppose
// et de moitie d'amplitude : on retranche a la reponse centrale la reponse
// de signe oppose de ses deux voisins en travers. Un lobe s'annule, une ligne
// garde la moitie de son amplitude, les deux moities d'un trait epais (meme
// signe) ne se retranchent rien, et une simple marche de luminance (bord
// d'ile UV) s'annule aussi : ce n'est pas un joint.
// La reponse doit ensuite se MAINTENIR le long de la ligne, dans la meme
// polarite : minimum sur cinq positions (0, +-3, +-6 texels le long), avec
// une tolerance d'un texel en travers aux positions decalees (une arete
// entre deux directions derive d'un texel sur six), puis maximum sur les
// directions et sur les deux polarites. Une ligne longue passe ; un point,
// un grain ou un tiret court tombent (les rivets de l'inox SEP sont des
// tirets de 2x4 texels qu'un detecteur a courte portee gardait).
//
// Calibre hors jeu sur SEP 3.3.2 avec Tools/seam_map.py, miroir numpy de ce
// fichier : seuils 0,10 et 0,25 (en log), densite moyenne de la carte 0,045 /
// 0,046 / 0,048 sur les tuiles des Blocks 3 / 1 / 2 (avant : 0,008 / 0,002 /
// 0,065), 93 a 103 % du reseau de l'une retrouve dans l'autre, 0,0001 a
// 0,003 sur les textures d'inox. Une cuisson coute environ 500 lectures par
// texel, une fois par texture. Toute modification ici se reporte dans
// Tools/seam_map.py.
Shader "Losket/SeamMap"
{
	Properties
	{
		_MainTex ("Texture de base (passe 0) ou pre-passe (passe 1)", 2D) = "white" {}
		_SeamLow ("Seuil bas (log-luminance)", Float) = 0.10
		_SeamHigh ("Seuil haut (log-luminance)", Float) = 0.25
		_SeamRadius ("Demi-distance des prelevements (texels d'origine)", Float) = 2
		_SeamSrcLod ("Niveau de mip lu dans la texture de base", Float) = 0
		_SeamSrcTexel ("Taille d'un texel de ce niveau (uv)", Vector) = (0, 0, 0, 0)
	}

	CGINCLUDE
	#include "UnityCG.cginc"

	// Plancher ajoute a la luminance avant le log : borne le bruit de
	// compression des zones presque noires. Log(0,03) = -3,51.
	#define LOSKET_LOG_EPS 0.03
	#define LOSKET_LOG_MIN -3.6
	#define LOSKET_LOG_RANGE 3.7

	sampler2D _MainTex;
	float4 _MainTex_TexelSize;
	float _SeamLow, _SeamHigh, _SeamRadius, _SeamSrcLod;
	float4 _SeamSrcTexel;

	// --- Passe 0 ---

	// Log-luminance d'un texel de la texture de base, ramenee dans 0..1.
	float LogLum(float2 uv)
	{
		float3 c = tex2Dlod(_MainTex, float4(uv, 0.0, _SeamSrcLod)).rgb;
		float l = max(dot(c, float3(0.299, 0.587, 0.114)), 0.0);
		return (log(l + LOSKET_LOG_EPS) - LOSKET_LOG_MIN) / LOSKET_LOG_RANGE;
	}

	float4 fragPre(v2f_img i) : SV_Target
	{
		// Le centre d'un texel de la cible tombe au coin commun de quatre
		// texels de la texture : un demi-texel dans chaque diagonale vise
		// leurs centres, ou le filtrage bilineaire rend la valeur exacte.
		float2 h = _SeamSrcTexel.xy * 0.5;
		float a = LogLum(i.uv + float2(-h.x, -h.y));
		float b = LogLum(i.uv + float2( h.x, -h.y));
		float c = LogLum(i.uv + float2(-h.x,  h.y));
		float d = LogLum(i.uv + float2( h.x,  h.y));
		return float4(0.25 * (a + b + c + d), min(min(a, b), min(c, d)), 0.0, 1.0);
	}

	// --- Passe 1 ---

	// Les deux champs de la pre-passe : r = moyenne, g = minimum.
	float2 Field(float2 uv)
	{
		return tex2Dlod(_MainTex, float4(uv, 0.0, 0.0)).rg;
	}

	// Reponse en travers signee au point p, pour les deux champs.
	float2 Across(float2 p, float2 across)
	{
		return Field(p) - 0.5 * (Field(p + across) + Field(p - across));
	}

	fixed4 fragSeam(v2f_img i) : SV_Target
	{
		// Un texel de la texture d'origine vaut un demi-texel de la pre-passe.
		float2 ts = _MainTex_TexelSize.xy * 0.5;
		float2 f0 = Field(i.uv);
		float best = 0.0;
		[loop]
		for (int k = 0; k < 12; k++) {
			float a = k * 0.2617994;
			float2 u = float2(cos(a), sin(a));
			float2 across = u * ts * _SeamRadius;
			float2 side = u * ts;
			float2 along = float2(-u.y, u.x) * ts;

			// Reponse centrale et celle des deux voisins en travers, dont on
			// ne retranche que la part de signe oppose (les lobes).
			float2 fp = Field(i.uv + across);
			float2 fm = Field(i.uv - across);
			float2 s0 = f0 - 0.5 * (fp + fm);
			float2 s1 = fp - 0.5 * (Field(i.uv + 2.0 * across) + f0);
			float2 s2 = fm - 0.5 * (f0 + Field(i.uv - 2.0 * across));
			float ridge = max(max(s0.r, 0.0) - max(max(-s1.r, -s2.r), 0.0), 0.0);
			float valley = max(max(-s0.g, 0.0) - max(max(s1.g, s2.g), 0.0), 0.0);

			// Maintien le long de la ligne, dans la meme polarite.
			[unroll]
			for (int s = 0; s < 4; s++) {
				float2 p = i.uv + along * ((s < 2 ? 3.0 : 6.0) * ((s & 1) == 0 ? 1.0 : -1.0));
				float2 c0 = Across(p, across);
				float2 c1 = Across(p + side, across);
				float2 c2 = Across(p - side, across);
				ridge = min(ridge, max(max(c0.r, max(c1.r, c2.r)), 0.0));
				valley = min(valley, max(-min(c0.g, min(c1.g, c2.g)), 0.0));
			}
			best = max(best, max(ridge, valley));
		}
		float seam = smoothstep(_SeamLow, _SeamHigh, best * LOSKET_LOG_RANGE);
		return fixed4(seam, seam, seam, 1.0);
	}
	ENDCG

	SubShader
	{
		Cull Off
		ZWrite Off
		ZTest Always

		// Passe 0 : pre-passe log-luminance (moyenne et minimum par bloc 2x2).
		Pass
		{
			CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment fragPre
			#pragma target 3.0
			ENDCG
		}

		// Passe 1 : detecteur de lignes longues sur la pre-passe.
		Pass
		{
			CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment fragSeam
			#pragma target 3.0
			ENDCG
		}
	}

	FallBack Off
}
