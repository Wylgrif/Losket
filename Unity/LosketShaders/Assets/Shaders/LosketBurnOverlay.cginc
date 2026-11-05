// Calcul du depot de Losket, commun aux deux shaders d'overlay :
//  - Losket/BurnOverlay          : passe avant seule (fondu alpha) ;
//  - Losket/BurnOverlayDeferred  : passe G-buffer + passe avant de repli.
// Le C# choisit l'un ou l'autre par overlay, d'apres le shader de la piece
// (LosketOverlayRig.PickShader). Ce fichier n'est pas un asset du bundle :
// Unity le compile dans chaque shader qui l'inclut.
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
// Trois motifs :
//  - Taches (0) : bruit isotrope, suie diffuse type Orion.
//  - Stries (1) : filaments alignes sur l'ecoulement DE SURFACE (composante
//    tangentielle du flux en chaque point), avec grappes de densite le long
//    de la coque et ligne de stagnation renforcee. Decomposition reprise de
//    la demo HTML de reference (Exemples/heatshield_generator.html) mais
//    evaluee par pixel : zero precalcul, zero cout par frame.
//  - Tuiles (2) : pas un bruit mais un TRANSPORT : depot blanc lache par
//    les joints entre tuiles, lus dans la texture de base de la piece, et
//    porte en aval le long du courant de surface, en espace UV, comme sur
//    le vrai Starship. Voir le bloc "Motif Tuiles" plus bas et
//    docs/traces-starship.md.
//
// Le bruit est echantillonne en espace motif (repere du vaisseau capture au
// premier vol) : stable face au Krakensbane, continu entre pieces voisines.
#ifndef LOSKET_BURN_OVERLAY_INCLUDED
#define LOSKET_BURN_OVERLAY_INCLUDED

#include "UnityCG.cginc"
#include "Lighting.cginc"

sampler2D _TemperLut;
sampler2D _BaseTex;
float4 _BaseTex_ST;
float _UseBaseAlpha;
fixed4 _SootColor;

// Espace "motif" : le repere du vaisseau capture au premier vol de la piece
// et fige dans la sauvegarde. Le bruit y est continu entre pieces voisines,
// colle a chaque piece pour toujours, et insensible au docking (chaque
// vaisseau garde le repere qu'il a capture).
float4x4 _ObjToPattern;

float _BurnMag, _PeakTemp, _TemperGain, _DirPower, _Spread, _Sharpness, _Streak;
float _NoiseScale, _Pattern, _Bleach, _Wrap, _RingMask, _Matte;
float4 _BurnDirW, _BurnDirO, _SpineAxisO;
float _FlowMin, _FlowRange, _SlantAft;
float4 _SpotPos, _SpotAxis;
float4x4 _ObjToPart;
float4 _EnvMin, _EnvMax;
float _SpotRadius, _SpotStretch, _SootGain, _SpotCut, _NoiseWarp;

// Motif Tuiles (depot par les joints) : carte de joints cuite depuis la
// texture de base (Losket/SeamMap, LosketOverlayRig.SeamMapFor), w = 1 si
// elle est posee, et reglages. Voir le bloc "Motif Tuiles".
sampler2D _SeamMap;
float4 _SeamMap_TexelSize;
float _SeamOn;
float _TileSize, _LeakFrac, _BrokenFrac, _StreakLength;
float _FanTan, _EdgeWear;

// Valeur de la suie en pleine couche : 1 - exp(-3), le plafond de la courbe
// de saturation ci-dessous. Sert a ramener la couverture a 1.
#define LOSKET_SOOT_FULL 0.9502

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
	float3 pPos : TEXCOORD4;
};

// Resultat du calcul de depot en un pixel, avant ecriture.
struct Deposit
{
	float3 n;          // normale monde
	fixed3 col;        // couleur du depot (suie / revenu), non eclairee
	float sootN;       // couverture de suie ramenee a 0..1 (1 = pleine couche)
	fixed3 temperRgb;  // teinte du revenu lue dans la LUT
	float temperA;     // opacite du revenu, deja reduite sous la suie
	float cut;         // masque de decoupe x enveloppe de depot
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
	// Interpolation quintique : la cubique laisse voir la grille du bruit
	// ("pixels") sur les grandes pieces.
	f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	return lerp(
		lerp(lerp(hash13(i + float3(0, 0, 0)), hash13(i + float3(1, 0, 0)), f.x),
		     lerp(hash13(i + float3(0, 1, 0)), hash13(i + float3(1, 1, 0)), f.x), f.y),
		lerp(lerp(hash13(i + float3(0, 0, 1)), hash13(i + float3(1, 0, 1)), f.x),
		     lerp(hash13(i + float3(0, 1, 1)), hash13(i + float3(1, 1, 1)), f.x), f.y),
		f.z);
}

// Trois valeurs par sommet de grille pour le prix d'un hash : sert de champ
// de deplacement a la deformation de domaine.
float3 hash33(float3 p)
{
	p = frac(p * float3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yxz + 33.33);
	return frac((p.xxy + p.yxx) * p.zyx);
}

float3 vnoise3(float3 p)
{
	float3 i = floor(p);
	float3 f = frac(p);
	f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	return lerp(
		lerp(lerp(hash33(i + float3(0, 0, 0)), hash33(i + float3(1, 0, 0)), f.x),
		     lerp(hash33(i + float3(0, 1, 0)), hash33(i + float3(1, 1, 0)), f.x), f.y),
		lerp(lerp(hash33(i + float3(0, 0, 1)), hash33(i + float3(1, 0, 1)), f.x),
		     lerp(hash33(i + float3(0, 1, 1)), hash33(i + float3(1, 1, 1)), f.x), f.y),
		f.z);
}

// Bruit a domaine deforme. Un bruit de valeur a ses contours alignes sur sa
// grille : apres seuillage on voit des marches d'escalier (les "pixels").
// Deux remedes cumules, sans changer la taille des details :
//  - tourner le repere, pour que la grille ne suive plus les axes de la
//    piece (rotation rigide : la statistique est inchangee) ;
//  - decaler le point d'echantillonnage par un bruit plus fin, d'une
//    fraction de cellule : les contours ondulent a petite echelle au lieu
//    de suivre les cellules.
// Le decalage est exprime en cellules du bruit appele : chaque octave est
// deformee a sa propre echelle. Deformer une seule fois pour toutes les
// octaves plie les plus fines sur elles-memes et donne un marbre, pas des
// taches (essaye, ecarte).
float wnoise(float3 p, float amount)
{
	const float3x3 turn = float3x3(
		 0.36, 0.48, -0.80,
		-0.80, 0.60,  0.00,
		 0.48, 0.64,  0.60);
	float3 q = mul(turn, p);
	q += (vnoise3(q * 1.7 + 7.3) - 0.5) * (0.8 * amount);
	return vnoise(q);
}

// ===================== Motif Tuiles : depot par les joints =====================
//
// Ce que le vrai Starship ramene de sa rentree n'est pas un bruit : c'est une
// poudre blanche (ablatif ou feutre sous les tuiles, brule par le plasma passe
// dans les interstices) deposee EN AVAL de quelques joints qui fuient et de
// tuiles cassees ; elle s'elargit en eventail, s'attenue et traverse les
// tuiles sans s'arreter aux joints. S'y ajoute un lisere blanc au bord amont
// de chaque tuile (revetement noir erode, tuile blanche dessous). Physique et
// sources : docs/traces-starship.md.
//
// Les tuiles sont la ou la TEXTURE les dessine. Une carte de joints est cuite
// une fois par texture de base (Losket/SeamMap : passe-haut de la luminance,
// RenderTexture avec mipmaps, LosketOverlayRig.SeamMapFor) ; une texture
// d'inox nu donne une carte vide, donc pas de tuiles sur l'inox, sans
// reglage. Une premiere version projetait une grille hexagonale sur une carte
// cylindrique de la piece : toute carte deforme des que la piece n'est pas
// un cylindre (nez, charnieres), et la grille ignorait les hexagones peints.
//
// Tout se calcule en espace UV de la piece :
//  1. base tangente du triangle sous le pixel par derivees d'ecran
//     (T = dpos/du, B = dpos/dv, exactes par triangle, sans tangentes de
//     maillage) ; le courant de surface (composante tangentielle du vent
//     relatif) se projette en direction UV, un pas de ds metres devient un
//     pas UV ;
//  2. sources : la carte de joints le long de la marche amont, lue au niveau
//     de mip dont le texel vaut le pas de marche (une ligne floutee a la
//     largeur du pas se somme a une constante : pas de crenelage), ponderee
//     par un champ aleatoire en cellules de la taille d'une tuile (peu de
//     zones fuient, celles qui fuient fuient fort) et par les tuiles cassees ;
//  3. transport : N pas, attenuation, eventail (trois prelevements), premier
//     pas decale par un bruit lisse (un hash par pixel scintille) ;
//  4. filaments alignes sur le courant, dans le plan tangent, en metres ;
//  5. usure de bord : le cote aval d'un joint, qui est le bord amont de la
//     tuile suivante, blanchit : carte(uv - v d) - carte(uv + v d).
// Limites : aux coutures UV la marche lit l'ile voisine sur une portee au
// plus ; la marche est droite en UV (pas de courbure des lignes de courant
// sur cette portee).

// Base tangente et courant en espace UV, calcules une fois par pixel.
struct TileFrame
{
	float2 uv;         // UV du pixel
	float2 flowUV;     // deplacement UV pour 1 m le long du courant
	float2 sideUV;     // deplacement UV pour 1 m en travers
	float3 t;          // courant de surface, espace motif, unitaire
	float3 side;       // travers, espace motif, unitaire
	float2 cellUV;     // taille UV d'une cellule grande comme une tuile
	float lod0;        // niveau de mip de la carte pour l'empreinte du pixel
	float texelsPerM;  // texels de la carte par metre le long du courant
	float pxm;         // metres par pixel sur la surface
	bool ok;
};

TileFrame TileBuildFrame(float3 sPos, float2 uv, float3 dl)
{
	TileFrame f;
	f.uv = uv;
	float3 dp1 = ddx(sPos), dp2 = ddy(sPos);
	float2 du1 = ddx(uv), du2 = ddy(uv);
	f.pxm = max(length(dp1), length(dp2));
	float det = du1.x * du2.y - du1.y * du2.x;
	f.ok = abs(det) > 1e-12;
	float inv = f.ok ? 1.0 / det : 0.0;
	float3 T = (dp1 * du2.y - dp2 * du1.y) * inv;   // dpos/du
	float3 B = (dp2 * du1.x - dp1 * du2.x) * inv;   // dpos/dv
	// Normale du triangle ; son signe est sans effet sur la projection.
	float3 n = normalize(cross(dp1, dp2));
	// Courant de surface : composante tangentielle du vent relatif (-dl).
	float3 t = -(dl - n * dot(dl, n));
	float tl = length(t);
	f.ok = f.ok && tl > 1e-4;
	t = t / max(tl, 1e-4);
	float3 side = normalize(cross(n, t));
	f.t = t;
	f.side = side;
	// Projection sur (T, B) par moindres carres (matrice de Gram).
	float tt = dot(T, T), bb = dot(B, B), tb = dot(T, B);
	float g = tt * bb - tb * tb;
	f.ok = f.ok && g > 1e-12;
	float gi = f.ok ? 1.0 / g : 0.0;
	float2 rt = float2(dot(t, T), dot(t, B));
	float2 rs = float2(dot(side, T), dot(side, B));
	f.flowUV = float2(bb * rt.x - tb * rt.y, tt * rt.y - tb * rt.x) * gi;
	f.sideUV = float2(bb * rs.x - tb * rs.y, tt * rs.y - tb * rs.x) * gi;
	// Cellules du tirage des fuites, en TEXELS de la carte (32 au reglage
	// par defaut), les memes pour tous les triangles. Calculees en metres
	// depuis la densite de texels de chaque triangle, elles sautaient d'un
	// triangle a l'autre sur le nez (rangees de triangles).
	f.cellUV = _SeamMap_TexelSize.xy * (32.0 * max(_TileSize, 0.02) / 0.25);
	// Niveau de mip pour l'empreinte du pixel, texels de la carte par metre.
	float2 fw = max(abs(du1), abs(du2)) * _SeamMap_TexelSize.zw;
	f.lod0 = max(0.0, log2(max(max(fw.x, fw.y), 1e-4)));
	f.texelsPerM = length(f.flowUV * _SeamMap_TexelSize.zw);
	return f;
}

float TileSeam(float2 uv, float lod)
{
	return tex2Dlod(_SeamMap, float4(uv, 0.0, lod)).r;
}

// Tirage par cellule grande comme une tuile : quelles zones fuient, quelles
// tuiles sont cassees. Sans identite de joint, c'est la ou le pixel tombe.
float TileCellHash(float2 uv, float2 cellUV, float salt)
{
	float2 c = floor(uv / cellUV);
	return hash13(float3(c, salt));
}

// Ce que la surface lache en un point de la carte, par metre de trajet.
float TileSources(TileFrame f, float2 uv, float lod)
{
	float seam = TileSeam(uv, lod);
	float leakFrac = max(_LeakFrac, 0.001);
	float leak = smoothstep(1.0 - leakFrac, 1.0, TileCellHash(uv, f.cellUV, 1.0));
	float broken = step(1.0 - _BrokenFrac, TileCellHash(uv, f.cellUV, 3.0));
	// Gain : un joint fait un centimetre (deux texels d'une texture de 4096
	// pour des hexagones de 51 texels), douze fois moins que la bande d'un
	// quart de tuile de l'ancienne grille.
	return seam * (leak * 18.0 + broken * 48.0);
}

// Depot transporte : remonte le courant en UV et somme les sources amont,
// attenuees avec la distance et diluees en eventail.
float TileDeposit(TileFrame f, float3 sPos)
{
	const int N = 16;
	float len = max(_StreakLength, 0.05);
	float ds = len / N;
	float decay = 0.75 * len;
	// Niveau de mip dont le texel vaut le pas de marche : une ligne floutee a
	// cette largeur se somme a une constante le long de la marche.
	float lod = max(f.lod0, log2(max(ds * f.texelsPerM, 1.0)));
	float jitter = vnoise(float3(sPos * 9.0 + 3.3));
	float2 q = f.uv - f.flowUV * (ds * jitter);
	float acc = 0.0;
	[loop] for (int k = 1; k <= N; k++) {
		q -= f.flowUV * ds;
		float s = (k + jitter) * ds;
		float2 lat = f.sideUV * (s * _FanTan);
		float taps = TileSources(f, q, lod) +
			0.5 * (TileSources(f, q + lat, lod) + TileSources(f, q - lat, lod));
		acc += taps * 0.5 * exp(-s / decay) * ds;
	}
	return acc;
}

// Filaments alignes sur le courant, dans le plan tangent et en metres.
// L'echelle suit le reglage d'echelle du motif, la nettete celui de nettete ;
// sous trois pixels de periode ils fondent vers leur moyenne.
float TileFilaments(TileFrame f, float3 sPos)
{
	float along = dot(sPos, f.t);
	float across = dot(sPos, f.side);
	float k = _NoiseScale * 7.0;
	float fade1 = saturate((0.33 - f.pxm * k) / 0.2);
	float fade2 = saturate((0.33 - f.pxm * k * 2.0) / 0.2);
	float n1 = lerp(0.5, vnoise(float3(across * k, along * k * 0.0636, 2.1)), fade1);
	float n2 = lerp(0.5, vnoise(float3(across * k * 2.0 + 3.3, along * k * 0.109 + 1.1, 5.7)), fade2);
	return pow(saturate(1.0 - abs(2.0 * n1 - 1.0) - 0.3 * n2), max(_Sharpness, 0.5));
}

// Usure de bord au pixel : le cote aval d'un joint est le bord amont de la
// tuile suivante, et c'est lui qui blanchit. Une tuile cassee montre un
// lisere epais sur son bord amont.
float TileWear(TileFrame f)
{
	float lod = f.lod0;
	// Un texel et demi de la carte, au niveau lu, le long du courant.
	float2 d = f.flowUV / max(f.texelsPerM, 1e-4) * (1.5 * exp2(lod));
	float up = TileSeam(f.uv - d, lod);
	float down = TileSeam(f.uv + d, lod);
	float edge = saturate(up - down);
	float var = 0.25 + 0.75 * smoothstep(0.5, 1.0, TileCellHash(f.uv, f.cellUV, 4.0));
	float broken = step(1.0 - _BrokenFrac, TileCellHash(f.uv, f.cellUV, 3.0));
	float chip = broken * saturate(TileSeam(f.uv - d * 2.0, lod + 2.0) * 3.0 - 0.3) * 1.5;
	return var * edge * 0.6 + chip;
}

// Couverture du motif Tuiles, a la place de grime, et densite de joints
// autour du pixel (0 sur l'inox nu, ~1 sur les tuiles) pour couper le revenu.
// Le depot transporte peut atteindre des faces moins exposees que ses
// sources : son masque est adouci ; l'usure suit la chauffe du pixel au carre.
float TileGrime(float3 sPos, float2 uv, float3 n, float3 dw, float3 dl, float mask,
                out float density)
{
	density = 0.0;
	TileFrame f = TileBuildFrame(sPos, uv, dl);
	if (_SeamOn < 0.5 || !f.ok) {
		return 0.0;
	}
	float dep = TileDeposit(f, sPos);
	float fil = TileFilaments(f, sPos);
	float wear = TileWear(f);
	// Les joints couvrent quelques pour cent de la surface d'une tuile : a un
	// niveau de mip grossier la carte vaut ~0,03 sur les tuiles SEP, ~0,004
	// sur leur inox (soudures).
	density = saturate(TileSeam(uv, max(f.lod0, 6.0)) * 40.0);
	float soft = saturate((dot(n, dw) + 2.0) / 3.0);
	float depMask = max(mask, soft * 0.6);
	return saturate(_BurnMag * (dep * (0.15 + 0.85 * fil) * 4.0 * depMask +
		_EdgeWear * wear * mask * mask));
}

v2f vert(appdata v)
{
	v2f o;
	o.pos = UnityObjectToClipPos(v.vertex);
	o.wNormal = UnityObjectToWorldNormal(v.normal);
	o.oPos = v.vertex.xyz;
	o.sPos = mul(_ObjToPattern, float4(v.vertex.xyz, 1.0)).xyz;
	o.pPos = mul(_ObjToPart, float4(v.vertex.xyz, 1.0)).xyz;
	o.uv = TRANSFORM_TEX(v.uv, _BaseTex);
	return o;
}

// Calcule le depot en un pixel : masque, motif, suie, revenu, decoupe.
Deposit ComputeDeposit(v2f i)
{
	Deposit d;
	float3 n = normalize(i.wNormal);
	float3 dw = normalize(_BurnDirW.xyz);
	float3 dl = normalize(_BurnDirO.xyz);
	d.n = n;

	// --- Masque directionnel enveloppant ---
	float facing = saturate((dot(n, dw) + _Wrap) / (1.0 + _Wrap));
	// Gerbe rasante du vide : anneau sur les flancs, un peu de depot
	// residuel dessus/dessous.
	float ring = 1.0 - 0.8 * abs(dot(n, dw));
	facing = lerp(facing, ring, _RingMask);
	float mask = pow(facing, _DirPower);

	// --- Gradient le long du flux : 1 au bord au vent, 0 a l'oppose.
	// _Spread <= 0.01 le desactive : un depot fige (poussiere) ne doit pas
	// dependre de la geometrie actuelle. ---
	if (_Spread > 0.01) {
		float proj = saturate((dot(i.sPos, dl) - _FlowMin) / max(_FlowRange, 1e-4));
		// Deux chauffes distinctes : l'ecoulement rasant, attenue en aval
		// par le gradient, et la chauffe d'INCIDENCE locale — bords
		// d'attaque des ailes, canards, derive, et toute surface oblique au
		// flux — jamais attenuee par la position. Le profil smoothstep
		// demarre des l'incidence faible et sature avant la perpendiculaire :
		// bande large autour des bords d'attaque, incidences intermediaires
		// bien nourries, seules les surfaces en incidence rasante restent
		// epargnees.
		float leading = smoothstep(0.05, 0.8, saturate(dot(n, dw)));
		mask = saturate(mask * pow(proj, _Spread) + leading * 0.8);
	}

	// --- Tache localisee : gaussienne autour du point d'impact, en espace
	// motif donc figee sur la piece. _SpotAxis est le sens de deplacement
	// des gaz a l'impact - distinct de dl, qui dit quelles faces sont
	// exposees : vers l'aval la tache s'etire en trainee, vers l'amont elle
	// reste ronde. Un impact de face ne montre que la partie ronde, un jet
	// rasant montre la trainee : la meme formule couvre les deux. Le bord
	// est module par un bruit large pour ne pas dessiner un disque parfait. ---
	if (_SpotPos.w > 0.5) {
		float3 jet = normalize(_SpotAxis.xyz);
		float3 rel = i.sPos - _SpotPos.xyz;
		float along = dot(rel, jet);
		float3 across = rel - jet * along;
		float reach = along > 0.0 ? max(_SpotStretch, 1.0) : 1.0;
		float3 ep = i.sPos * 1.3 + 11.7;
		float edgeNoise = vnoise(ep);
		if (_NoiseWarp > 0.001) {
			// Une seule octave de bruit dessinait un contour en marches :
			// deformee et doublee d'une octave fine.
			edgeNoise = wnoise(ep, _NoiseWarp) * 0.8
			          + wnoise(ep * 2.3 + 5.1, _NoiseWarp) * 0.2;
		}
		float edge = 0.75 + 0.5 * edgeNoise;
		float rad = max(_SpotRadius, 0.05) * edge;
		float d2 = dot(across, across) + (along / reach) * (along / reach);
		float spot = exp(-d2 / (rad * rad));
		// Coupe du bord : fondu court entre la moitie du seuil et le seuil,
		// puis plus rien. Sans elle la queue de la gaussienne laisse un halo
		// de revenu loin du point chaud.
		if (_SpotCut > 0.001) {
			spot *= smoothstep(0.5 * _SpotCut, _SpotCut, spot);
		}
		mask *= spot;
	}

	// --- Motif taches : bruit isotrope, legerement etire. Trois octaves
	// decalees pour casser la grille sur les grandes pieces. ---
	float3 p = i.sPos * _NoiseScale;
	float3 pb = p - dl * dot(p, dl) * (1.0 - 1.0 / _Streak);
	float blob;
	if (_NoiseWarp > 0.001) {
		blob = wnoise(pb, _NoiseWarp) * 0.5
		     + wnoise(pb * 2.63 + 17.3, _NoiseWarp) * 0.32
		     + wnoise(pb * 5.71 + 31.9, _NoiseWarp) * 0.18;
	} else {
		blob = vnoise(pb) * 0.5
		     + vnoise(pb * 2.63 + 17.3) * 0.32
		     + vnoise(pb * 5.71 + 31.9) * 0.18;
	}
	float blobGrime = pow(saturate(mask * _BurnMag * (0.45 + 1.1 * blob) * 1.6), _Sharpness);

	// --- Motif stries : decomposition de la demo de reference ---
	// La colonne vertebrale est la LIGNE de stagnation d'un corps allonge
	// frappe de biais : elle court le long de la piece, perpendiculaire au
	// flux (_SpineAxisO, calcule par le C# depuis la geometrie du maillage
	// et la direction de rentree). Les marques en emanent lateralement et
	// derivent vers l'aval. La carte (h, w) est globale et signee : pas de
	// repere derive de la normale, qui se retournerait a la ligne de
	// stagnation et mettrait le motif en miroir (bug du "Rorschach"). Seule
	// couture residuelle : theta = ±pi, face abritee, masque deja nul.
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

	// Grappes de densite LE LONG de la colonne : certaines bandes strient
	// fort, d'autres presque pas ("dispersion verticale").
	float dens = smoothstep(0.3, 0.75, vnoise(float3(h * _NoiseScale * 0.55, 3.3, 41.7)));

	// Colonne vertebrale : etroite autour de theta = 0, modulee par les
	// grappes comme dans la demo.
	float spine = pow(saturate(cos(theta)), 24.0) * (0.4 + 0.6 * dens);

	float streakGrime = saturate(mask * _BurnMag *
		(fil * (0.25 + 0.75 * dens) * 2.0 + spine));
	// Legere suie diffuse sous la colonne, pour asseoir les stries.
	streakGrime = max(streakGrime, saturate(blobGrime * 0.3 * (spine + 0.3)));

	float grime = lerp(blobGrime, streakGrime, saturate(_Pattern));
	// Motif Tuiles : depot blanc transporte depuis les joints, a la place
	// des bruits. Branche uniforme : rien n'est calcule quand il est eteint.
	float tileDensity = 0.0;
	[branch] if (_Pattern > 1.5) {
		grime = TileGrime(i.sPos, i.uv, n, dw, dl, mask, tileDensity);
	}
	float soot = (1.0 - exp(-3.0 * grime)) * saturate(_SootGain);

	// --- Revenu : la teinte vient de la LUT, jamais d'un slider ---
	float noise = lerp(blob, svn, saturate(_Pattern));
	float lutU = saturate(_PeakTemp * (0.55 + 0.45 * mask) + 0.12 * (noise - 0.5));
	fixed4 temper = tex2D(_TemperLut, float2(lutU, 0.5));
	d.temperRgb = temper.rgb;
	// Motif Tuiles : pas de revenu sur les tuiles elles-memes (ceramique),
	// seulement sur l'inox, la ou la carte de joints est vide.
	d.temperA = temper.a * _TemperGain *
		smoothstep(0.02, 0.25, mask * _BurnMag) * (1.0 - soot) * (1.0 - tileDensity);

	// --- Couleur du depot : suie noire ou trace blanchie ---
	fixed3 deposit = lerp(_SootColor.rgb, fixed3(0.93, 0.91, 0.88), _Bleach);
	d.col = lerp(temper.rgb, deposit, soot);
	d.sootN = saturate(soot / LOSKET_SOOT_FULL);

	// Pieces ajourees : rien ne se depose dans les trous. Decision SEUILLEE,
	// comme le clip() du shader de base, et non multiplication par l'alpha
	// brut : sur les shaders cutout, les zones pleines peuvent porter un
	// alpha moyen (0.5-0.6, cas des ailes stock) qui attenuerait voire
	// eteindrait le depot alors qu'elles sont parfaitement opaques a l'ecran.
	float cutMask = smoothstep(0.25, 0.45, tex2D(_BaseTex, i.uv).a);
	d.cut = lerp(1.0, cutMask, _UseBaseAlpha);

	// Enveloppe de depot : fondu sur 10 cm au-dela de la boite, pour ne pas
	// tracer une ligne nette sur ce qui la traverse.
	if (_EnvMin.w > 0.5) {
		float3 over = max(_EnvMin.xyz - i.pPos, i.pPos - _EnvMax.xyz);
		float outside = max(over.x, max(over.y, over.z));
		d.cut *= 1.0 - smoothstep(0.0, 0.1, outside);
	}

	return d;
}

// --- Passe avant : fondu alpha sur l'image deja eclairee ---
// En rendu avant, le seul moyen d'eteindre un reflet est de couvrir le
// pixel : une couche mate complete est opaque. Un depot "brillant" laisse
// 15 % de la surface d'origine, reflets compris, transparaitre meme en
// pleine couche.
fixed4 fragForward(v2f i) : SV_Target
{
	Deposit d = ComputeDeposit(i);

	float sootAlpha = d.sootN * lerp(0.85, 1.0, _Matte);
	float alpha = saturate(max(sootAlpha, d.temperA)) * d.cut;

	// Eclairage minimal : ambiante + directionnelle principale.
	float ndl = saturate(dot(d.n, _WorldSpaceLightPos0.xyz));
	float3 light = ShadeSH9(float4(d.n, 1)) + _LightColor0.rgb * ndl;
	fixed3 col = d.col * light;

	return fixed4(col, alpha);
}

// --- Passe differee : decal dans le G-buffer standard d'Unity ---
//   0 : albedo rgb + occlusion a   -> fondu alpha, occlusion conservee
//   1 : speculaire rgb + lissage a -> multiplies : x(1 - couverture)
//   2 : normale                     -> intacte (celle de la piece)
//   3 : emission                    -> intacte (lumieres, chauffe)
// Les fondus par cible sont declares dans la passe du shader.
struct GBuffer
{
	half4 diffuse : SV_Target0;
	half4 spec : SV_Target1;
	half4 normal : SV_Target2;
	half4 emission : SV_Target3;
};

GBuffer fragDeferred(v2f i)
{
	Deposit d = ComputeDeposit(i);
	GBuffer o;

	// Albedo : couleur du depot, fondue a hauteur de la couverture de suie
	// ou de l'opacite du revenu.
	o.diffuse = half4(d.col, max(d.sootN, d.temperA) * d.cut);

	// Speculaire et lissage. La suie mate les eteint a proportion de sa
	// couverture (alpha de cette cible = 1 - facteur applique au lissage).
	// Le revenu teinte le speculaire de la couleur de l'oxyde, normalisee
	// pour ne pas l'assombrir : un acier bleui reflechit en bleu, mais
	// reflechit toujours.
	float matteCov = d.sootN * _Matte * d.cut;
	float tintMax = max(d.temperRgb.r, max(d.temperRgb.g, d.temperRgb.b));
	float3 tint = d.temperRgb / max(tintMax, 0.3);
	float3 specMul = lerp(float3(1, 1, 1), tint, d.temperA * d.cut) * (1.0 - matteCov);
	o.spec = half4(specMul, matteCov);

	// Cibles masquees par ColorMask : valeurs sans effet.
	o.normal = half4(0, 0, 0, 0);
	o.emission = half4(0, 0, 0, 0);
	return o;
}

#endif
