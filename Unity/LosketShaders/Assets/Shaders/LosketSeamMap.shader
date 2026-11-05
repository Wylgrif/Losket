// Carte de joints du motif Tuiles : detecteur de LIGNES sur la luminance de
// la texture de base, cuit UNE fois par texture dans une RenderTexture avec
// mipmaps (LosketOverlayRig.SeamMapFor, Graphics.Blit). Les interstices
// entre tuiles, clairs ou sombres, ressortent ; une texture d'inox nu donne
// une carte vide, donc pas de tuiles sur l'inox, sans aucun reglage. Les
// lignes de panneaux ressortent aussi : en vrai elles fuient aussi.
//
// Dans douze directions (les joints d'hexagones sont a 60 degres), la
// reponse en travers r = |c - (a + b) / 2|, a et b a _SeamRadius texels de
// part et d'autre, doit se MAINTENIR le long de la ligne : on garde le
// minimum de r sur cinq positions (0, +-3, +-6 texels le long), avec une
// tolerance d'un texel en travers aux positions decalees (une arete entre
// deux directions derive d'un texel sur six), puis le maximum sur les
// directions. Une ligne longue passe, un point, un grain ou un tiret court
// tombent. Un passe-haut isotrope prenait le grain de
// rivets de la texture d'inox SEP pour des joints (taches blanches sur la
// coque), et un detecteur a courte portee ses tirets de 2x4 texels.
// Calibre hors jeu sur SEP : joints clairs de 1 a 2 texels dans une
// texture de 4096, rayon 2, seuils 0,08 et 0,20 (coque : 0 % de texels
// marques hors soudures ; tuiles : 84 % des contours gardes, contre 32 %
// sans la tolerance). Une cuisson coute environ 470 lectures par texel,
// une fois par texture.
Shader "Losket/SeamMap"
{
	Properties
	{
		_MainTex ("Texture de base", 2D) = "white" {}
		_SeamLow ("Seuil bas de la lignite", Float) = 0.08
		_SeamHigh ("Seuil haut de la lignite", Float) = 0.20
		_SeamRadius ("Demi-distance des prelevements (texels)", Float) = 2
	}

	SubShader
	{
		Cull Off
		ZWrite Off
		ZTest Always

		Pass
		{
			CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment frag
			#pragma target 3.0
			#include "UnityCG.cginc"

			sampler2D _MainTex;
			float4 _MainTex_TexelSize;
			float _SeamLow, _SeamHigh, _SeamRadius;

			float Lum(float2 uv)
			{
				fixed3 c = tex2D(_MainTex, uv).rgb;
				return dot(c, fixed3(0.299, 0.587, 0.114));
			}

			// Reponse en travers de la direction u au point p.
			float Response(float2 p, float2 across)
			{
				return abs(Lum(p) - 0.5 * (Lum(p + across) + Lum(p - across)));
			}

			fixed4 frag(v2f_img i) : SV_Target
			{
				float2 ts = _MainTex_TexelSize.xy;
				float best = 0.0;
				[loop]
				for (int k = 0; k < 12; k++) {
					float a = k * 0.2617994;
					float2 u = float2(cos(a), sin(a));
					float2 across = u * ts * _SeamRadius;
					float2 side = u * ts;
					float2 along = float2(-u.y, u.x) * ts;
					float r = Response(i.uv, across);
					[unroll]
					for (int s = 0; s < 4; s++) {
						float2 p = i.uv + along * ((s < 2 ? 3.0 : 6.0) * ((s & 1) == 0 ? 1.0 : -1.0));
						float rs = max(Response(p, across),
							max(Response(p + side, across), Response(p - side, across)));
						r = min(r, rs);
					}
					best = max(best, r);
				}
				float seam = smoothstep(_SeamLow, _SeamHigh, best);
				return fixed4(seam, seam, seam, 1.0);
			}
			ENDCG
		}
	}

	FallBack Off
}
