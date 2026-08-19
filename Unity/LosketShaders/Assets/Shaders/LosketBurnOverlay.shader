// Passe overlay de brulure (voie B) : rendue par-dessus le materiau d'origine
// de la piece, quel que soit son shader de base.
//
// Le masque combine deux termes, calibres sur les photos de reference :
//  - un terme d'orientation "enveloppant" (wrap) : les faces face au flux
//    brulent a fond, les flancs partiellement, la face abritee pas du tout.
//    Un dot() pur laisserait les flancs vierges, ce qui ne correspond pas a
//    Orion, dont les parois laterales sont noircies pres du bouclier.
//  - un gradient positionnel le long de l'axe du flux : plus on s'eloigne du
//    bord au vent, moins ca brule. C'est lui qui donne le degrade vertical
//    d'Orion ("plus en bas qu'en haut, uniforme a une meme hauteur").
//
// Deux motifs de bruit :
//  - Taches (0) : bruit isotrope, suie diffuse type Orion.
//  - Stries (1) : filaments alignes sur l'ecoulement DE SURFACE (composante
//    tangentielle du flux en chaque point), avec grappes de densite le long
//    de la coque et ligne de stagnation renforcee. Decomposition reprise de
//    la demo HTML de reference (Exemples/heatshield_generator.html) mais
//    evaluee par pixel : zero precalcul, zero cout par frame.
//
// Le bruit est echantillonne en espace objet du maillage : stable face au
// Krakensbane. Le passage en espace-vaisseau (continuite entre pieces)
// viendra avec la vraie simulation.
Shader "Losket/BurnOverlay"
{
	Properties
	{
		_TemperLut ("LUT de revenu (temperature -> teinte)", 2D) = "white" {}
		_SootColor ("Couleur de la suie", Color) = (0.06, 0.055, 0.05, 1)

		// Masque de decoupe pour les pieces a texture ajouree (parachutes,
		// poutrelles, grilles) : l'alpha de la texture d'origine limite le depot
		// aux zones reellement opaques. Active uniquement quand le shader de
		// base est de type cutout/transparent — sur les shaders opaques de KSP,
		// l'alpha de _MainTex encode la specularite, pas la decoupe.
		_BaseTex ("Texture de base (masque alpha)", 2D) = "white" {}
		_UseBaseAlpha ("Utiliser l'alpha de la base", Range(0, 1)) = 0

		_BurnMag ("Intensite de brulure", Range(0, 1)) = 0.5
		_PeakTemp ("Temperature de pointe (normalisee)", Range(0, 1)) = 0.65
		// Visibilite du revenu. La TEINTE vient toujours de _PeakTemp via la
		// LUT (physique) ; ce gain ne joue que sur l'opacite. Le multiplier a
		// la temperature etait une erreur : il decalait la lecture de la LUT
		// vers la bande jaune paille au lieu d'attenuer l'effet.
		_TemperGain ("Visibilite du revenu", Range(0, 1)) = 1
		_DirPower ("Concentration directionnelle", Range(0.2, 8)) = 2
		_Spread ("Etalement le long du flux", Range(0.2, 8)) = 1.5
		_Sharpness ("Nettete des bords", Range(0.5, 8)) = 2
		_Streak ("Anisotropie (stries le long du flux)", Range(1, 16)) = 1
		_NoiseScale ("Echelle du bruit", Range(0.5, 16)) = 4
		_Pattern ("Motif (0 = taches, 1 = stries)", Range(0, 1)) = 0
		_Bleach ("Eclaircissement (0 = suie noire, 1 = trace blanche)", Range(0, 1)) = 0
		// A 1 : les faces perpendiculaires au flux brulent a moitie. A 2 : meme
		// les faces opposees au flux recoivent un tiers de la dose — la suie
		// "remonte" sur les flancs inclines, comme sur Orion.
		_Wrap ("Enveloppement du masque directionnel", Range(0, 2)) = 1.2

		// Renseignes par le module C# a chaque frame, pas par l'utilisateur.
		_BurnDirW ("Direction du flux, espace monde", Vector) = (0, -1, 0, 0)
		_BurnDirO ("Direction du flux, espace objet", Vector) = (0, -1, 0, 0)
		_FlowMin ("Projection minimale du maillage sur le flux", Float) = -1
		_FlowRange ("Etendue du maillage le long du flux", Float) = 2
		_SpineAxisO ("Axe de la ligne de stagnation, espace objet", Vector) = (0, 1, 0, 0)
		_SlantAft ("Derive des stries vers l'aval", Float) = 0
	}

	SubShader
	{
		Tags {
			"Queue" = "Geometry+100"
			"RenderType" = "Transparent"
			"IgnoreProjector" = "True"
		}

		Pass
		{
			Tags { "LightMode" = "ForwardBase" }

			Blend SrcAlpha OneMinusSrcAlpha
			ZWrite Off
			Offset -1, -1

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			#include "Lighting.cginc"

			sampler2D _TemperLut;
			sampler2D _BaseTex;
			float4 _BaseTex_ST;
			float _UseBaseAlpha;
			fixed4 _SootColor;

			// Espace "motif" : le repere du vaisseau capture au premier vol de
			// la piece et fige dans la sauvegarde. Le bruit y est continu entre
			// pieces voisines, colle a chaque piece pour toujours, et insensible
			// au docking (chaque vaisseau garde le repere qu'il a capture).
			float4x4 _ObjToPattern;
			float _BurnMag, _PeakTemp, _TemperGain, _DirPower, _Spread, _Sharpness, _Streak;
			float _NoiseScale, _Pattern, _Bleach, _Wrap;
			float4 _BurnDirW, _BurnDirO, _SpineAxisO;
			float _FlowMin, _FlowRange, _SlantAft;

			struct appdata
			{
				float4 vertex : POSITION;
				float3 normal : NORMAL;
				float2 uv : TEXCOORD0;
			};

			struct v2f
			{
				float4 pos : SV_POSITION;
				float3 wNormal : TEXCOORD0;
				float3 oPos : TEXCOORD1;
				float3 sPos : TEXCOORD2;
				float2 uv : TEXCOORD3;
			};

			// Hash sans sinus (precision stable sur tous les GPU).
			float hash13(float3 p)
			{
				p = frac(p * 0.1031);
				p += dot(p, p.yzx + 33.33);
				return frac((p.x + p.y) * p.z);
			}

			float vnoise(float3 p)
			{
				float3 i = floor(p);
				float3 f = frac(p);
				// Interpolation quintique : la cubique laisse voir la grille du
				// bruit ("pixels") sur les grandes pieces.
				f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
				return lerp(
					lerp(lerp(hash13(i + float3(0, 0, 0)), hash13(i + float3(1, 0, 0)), f.x),
					     lerp(hash13(i + float3(0, 1, 0)), hash13(i + float3(1, 1, 0)), f.x), f.y),
					lerp(lerp(hash13(i + float3(0, 0, 1)), hash13(i + float3(1, 0, 1)), f.x),
					     lerp(hash13(i + float3(0, 1, 1)), hash13(i + float3(1, 1, 1)), f.x), f.y),
					f.z);
			}

			v2f vert(appdata v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.wNormal = UnityObjectToWorldNormal(v.normal);
				o.oPos = v.vertex.xyz;
				o.sPos = mul(_ObjToPattern, float4(v.vertex.xyz, 1.0)).xyz;
				o.uv = TRANSFORM_TEX(v.uv, _BaseTex);
				return o;
			}

			fixed4 frag(v2f i) : SV_Target
			{
				float3 n = normalize(i.wNormal);
				float3 dw = normalize(_BurnDirW.xyz);
				float3 dl = normalize(_BurnDirO.xyz);

				// --- Masque directionnel enveloppant ---
				float facing = saturate((dot(n, dw) + _Wrap) / (1.0 + _Wrap));
				float mask = pow(facing, _DirPower);

				// --- Gradient le long du flux : 1 au bord au vent, 0 a l'oppose.
				// _Spread <= 0.01 le desactive : un depot fige (poussiere) ne
				// doit pas dependre de la geometrie actuelle. ---
				if (_Spread > 0.01) {
					float proj = saturate((dot(i.sPos, dl) - _FlowMin) / max(_FlowRange, 1e-4));
					// Deux chauffes distinctes : l'ecoulement rasant, attenue en
					// aval par le gradient, et la chauffe d'INCIDENCE locale —
					// bords d'attaque des ailes, canards, derive, et toute
					// surface oblique au flux — jamais attenuee par la position.
					// Le profil smoothstep demarre des l'incidence faible et
					// sature avant la perpendiculaire : bande large autour des
					// bords d'attaque, incidences intermediaires bien nourries,
					// seules les surfaces en incidence rasante restent epargnees.
					float leading = smoothstep(0.05, 0.8, saturate(dot(n, dw)));
					mask = saturate(mask * pow(proj, _Spread) + leading * 0.8);
				}

				// --- Motif taches : bruit isotrope, legerement etire. Trois
				// octaves decalees pour casser la grille sur les grandes pieces. ---
				float3 p = i.sPos * _NoiseScale;
				float3 pb = p - dl * dot(p, dl) * (1.0 - 1.0 / _Streak);
				float blob = vnoise(pb) * 0.5
				           + vnoise(pb * 2.63 + 17.3) * 0.32
				           + vnoise(pb * 5.71 + 31.9) * 0.18;
				float blobGrime = pow(saturate(mask * _BurnMag * (0.45 + 1.1 * blob) * 1.6), _Sharpness);

				// --- Motif stries : decomposition de la demo de reference ---
				// La colonne vertebrale est la LIGNE de stagnation d'un corps
				// allonge frappe de biais : elle court le long de la piece,
				// perpendiculaire au flux (_SpineAxisO, calcule par le C# depuis
				// la geometrie du maillage et la direction de rentree). Les
				// marques en emanent lateralement et derivent vers l'aval.
				// La carte (h, w) est globale et signee : pas de repere derive de
				// la normale, qui se retournerait a la ligne de stagnation et
				// mettrait le motif en miroir (bug du "Rorschach"). Seule couture
				// residuelle : theta = ±pi, face abritee, masque deja nul.
				float3 spineA = normalize(_SpineAxisO.xyz);
				float3 sideA = normalize(cross(dl, spineA));
				float h = dot(i.sPos, spineA);                    // position le long de la colonne
				float3 rp = i.sPos - spineA * h;
				float theta = atan2(dot(rp, sideA), dot(rp, dl)); // 0 = face au vent
				float w = theta * max(length(rp), 0.05);          // arc lateral signe
				float hh = h - abs(w) * _SlantAft;                // derive aval en s'ecartant

				float ac = hh * _NoiseScale * 2.6;                // fin le long de la colonne
				float al = w * _NoiseScale * 2.6 / max(_Streak, 1.0); // long en s'ecartant
				float svn = vnoise(float3(ac, al, 1.7));
				float sv2 = vnoise(float3(ac * 2.13 + 5.2, al * 1.7 + 8.8, 7.3));
				float fil = pow(saturate(1.0 - abs(2.0 * svn - 1.0) - 0.25 * sv2), 1.0 + _Sharpness);

				// Grappes de densite LE LONG de la colonne : certaines bandes
				// strient fort, d'autres presque pas ("dispersion verticale").
				float dens = smoothstep(0.3, 0.75, vnoise(float3(h * _NoiseScale * 0.55, 3.3, 41.7)));

				// Colonne vertebrale : etroite autour de theta = 0, modulee par
				// les grappes comme dans la demo.
				float spine = pow(saturate(cos(theta)), 24.0) * (0.4 + 0.6 * dens);

				float streakGrime = saturate(mask * _BurnMag *
					(fil * (0.25 + 0.75 * dens) * 2.0 + spine));
				// Legere suie diffuse sous la colonne, pour asseoir les stries.
				streakGrime = max(streakGrime, saturate(blobGrime * 0.3 * (spine + 0.3)));

				float grime = lerp(blobGrime, streakGrime, saturate(_Pattern));
				float soot = 1.0 - exp(-3.0 * grime);

				// --- Revenu : la teinte vient de la LUT, jamais d'un slider ---
				float noise = lerp(blob, svn, saturate(_Pattern));
				float lutU = saturate(_PeakTemp * (0.55 + 0.45 * mask) + 0.12 * (noise - 0.5));
				fixed4 temper = tex2D(_TemperLut, float2(lutU, 0.5));
				float temperA = temper.a * _TemperGain *
					smoothstep(0.02, 0.25, mask * _BurnMag) * (1.0 - soot);

				// --- Couleur du depot : suie noire ou trace blanchie ---
				fixed3 deposit = lerp(_SootColor.rgb, fixed3(0.93, 0.91, 0.88), _Bleach);
				fixed3 col = lerp(temper.rgb, deposit, soot);
				float alpha = saturate(max(soot * 0.95, temperA));

				// Pieces ajourees : rien ne se depose dans les trous. Decision
				// SEUILLEE, comme le clip() du shader de base, et non multiplication
				// par l'alpha brut : sur les shaders cutout, les zones pleines
				// peuvent porter un alpha moyen (0.5-0.6, cas des ailes stock) qui
				// attenuerait voire eteindrait le depot alors qu'elles sont
				// parfaitement opaques a l'ecran.
				float cutMask = smoothstep(0.25, 0.45, tex2D(_BaseTex, i.uv).a);
				alpha *= lerp(1.0, cutMask, _UseBaseAlpha);

				// Eclairage minimal : ambiante + directionnelle principale.
				float ndl = saturate(dot(n, _WorldSpaceLightPos0.xyz));
				float3 light = ShadeSH9(float4(n, 1)) + _LightColor0.rgb * ndl;
				col *= light;

				return fixed4(col, alpha);
			}
			ENDCG
		}
	}

	FallBack Off
}
