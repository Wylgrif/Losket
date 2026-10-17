// Passe overlay de brulure (voie B) : rendue par-dessus le materiau d'origine
// de la piece, quel que soit son shader de base, par fondu alpha sur l'image
// deja eclairee. Tout le calcul du depot est dans LosketBurnOverlay.cginc.
//
// C'est la variante "avant", celle de toujours. Elle sert :
//  - sans le mod Deferred (camera en rendu avant) ;
//  - avec Deferred, pour les pieces dont le shader de base n'a pas de passe
//    differee (TexturesUnlimited 1.5, Shaddy, vieux decals...) : Unity les
//    dessine en avant, APRES l'eclairage differe, et un decal G-buffer serait
//    recouvert par elles. Le C# (LosketOverlayRig.PickShader) choisit entre
//    cette variante et Losket/BurnOverlayDeferred d'apres le shader de base.
//
// En rendu avant, on ne peut attenuer un reflet qu'en couvrant le pixel :
// le reglage _Matte porte l'opacite de la suie a 1 en pleine couche.
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

		// Depot lateral : dans le vide, la poussiere est projetee en gerbe
		// rasante (videos Apollo) et frappe les FLANCS, pas le dessous. A 1, le
		// masque directionnel devient un anneau perpendiculaire a la direction
		// d'arrivee. Alimente par la fraction de dose deposee dans le vide.
		_RingMask ("Depot lateral (vide)", Range(0, 1)) = 0

		// Reflets sous le depot. A 1 (suie, poussiere), la couche est mate :
		// elle eteint le speculaire et le lissage de la surface a proportion de
		// sa couverture - une surface a moitie couverte garde la moitie de son
		// reflet, une surface couverte n'en garde rien. A 0 (metal brule), le
		// depot colore la surface mais la laisse aussi brillante qu'avant. Le
		// revenu n'est jamais mat : c'est un film d'oxyde sur un metal poli.
		// En rendu avant, seule l'opacite maximale de la suie en depend.
		_Matte ("Reflets eteints par le depot", Range(0, 1)) = 1

		// Renseignes par le module C# a chaque frame, pas par l'utilisateur.
		_BurnDirW ("Direction du flux, espace monde", Vector) = (0, -1, 0, 0)
		_BurnDirO ("Direction du flux, espace objet", Vector) = (0, -1, 0, 0)
		_FlowMin ("Projection minimale du maillage sur le flux", Float) = -1
		_FlowRange ("Etendue du maillage le long du flux", Float) = 2
		_SpineAxisO ("Axe de la ligne de stagnation, espace objet", Vector) = (0, 1, 0, 0)
		_SlantAft ("Derive des stries vers l'aval", Float) = 0

		// Tache localisee (panache de moteur) : le depot est concentre autour
		// d'un point d'impact fige en espace motif, etire vers l'aval du jet.
		// w = 0 desactive le masque (brulure de rentree, poussiere).
		_SpotPos ("Point d'impact, espace motif (w = actif)", Vector) = (0, 0, 0, 0)
		_SpotAxis ("Sens du jet au point d'impact, espace motif", Vector) = (0, -1, 0, 0)
		_SpotRadius ("Rayon de la tache (m)", Float) = 1
		_SpotStretch ("Etirement de la tache vers l'aval", Range(1, 8)) = 1
		// Part de suie dans le depot : a 1 la suie couvre tout, plus bas elle
		// laisse voir le revenu (tuyere bleuie par son propre fonctionnement).
		_SootGain ("Part de suie", Range(0, 1)) = 1
		// Coupe nette du bord de la tache : sous ce niveau de la gaussienne,
		// plus rien. 0 = bord libre. Sert aux tuyeres marquees par elles-memes,
		// dont la teinte ne doit pas baver sur le bati du moteur.
		_SpotCut ("Coupe du bord de la tache", Range(0, 1)) = 0

		// Deformation de domaine du bruit : les points d'echantillonnage du
		// motif sont decales par un bruit plus fin, dans un repere tourne. Le
		// motif garde sa taille mais ses contours ne suivent plus la grille du
		// bruit (les "pixels"). 0 = bruit d'origine. Verifie hors jeu par
		// Tools/render_pattern.py, portage du motif en Python.
		_NoiseWarp ("Deformation du bruit", Range(0, 1)) = 0

		// Enveloppe de depot, en espace PIECE : la boite qui englobait la
		// geometrie d'une piece deployable quand elle s'est marquee repliee.
		// Ce qui en sort reste propre (panneau solaire deplie apres coup).
		// w de _EnvMin = 1 pour activer.
		_EnvMin ("Enveloppe, coin min (w = actif)", Vector) = (0, 0, 0, 0)
		_EnvMax ("Enveloppe, coin max", Vector) = (0, 0, 0, 0)
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
			Name "FORWARD"
			Tags { "LightMode" = "ForwardBase" }

			Blend SrcAlpha OneMinusSrcAlpha
			ZWrite Off
			Offset -1, -1

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment fragForward
			#include "LosketBurnOverlay.cginc"
			ENDCG
		}
	}

	FallBack Off
}
