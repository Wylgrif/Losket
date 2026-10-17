// Variante differee de l'overlay de brulure : decal dans le G-buffer.
// Tout le calcul du depot est dans LosketBurnOverlay.cginc ; les proprietes
// sont les memes que Losket/BurnOverlay (voir ce fichier pour leur sens), de
// sorte qu'un materiau peut passer d'une variante a l'autre sans rien perdre.
//
// Le C# (LosketOverlayRig.PickShader) choisit cette variante pour les pieces
// dont le shader de base a lui-meme une passe differee : shaders de
// remplacement du mod Deferred, TexturesUnlimited 1.6. Unity rend alors la
// passe "Deferred" dans le G-buffer, juste apres la piece (file Geometry+100,
// au-dela de la sienne) : l'albedo est fondu vers la couleur du depot, et le
// speculaire et le lissage sont REDUITS la ou la suie ou la poussiere
// couvrent (reglage _Matte). C'est l'eclairage differe qui eclaire ensuite la
// suie, avec la normale et les ombres de la piece - et un reflet ne peut plus
// traverser une couche de poussiere, puisqu'il n'est plus calcule. Le revenu,
// lui, teinte le speculaire sans le reduire : un acier bleui reste un miroir
// (Exemples/metal.jpg).
//
// La passe "ForwardBase" reste en repli : camera en rendu avant (pas de mod
// Deferred), ou overlay pousse au-dela de la file 2500 par une piece de base
// transparente. Unity choisit la passe d'apres la camera et la file, sans
// intervention du C#.
Shader "Losket/BurnOverlayDeferred"
{
	Properties
	{
		_TemperLut ("LUT de revenu (temperature -> teinte)", 2D) = "white" {}
		_SootColor ("Couleur de la suie", Color) = (0.06, 0.055, 0.05, 1)
		_BaseTex ("Texture de base (masque alpha)", 2D) = "white" {}
		_UseBaseAlpha ("Utiliser l'alpha de la base", Range(0, 1)) = 0

		_BurnMag ("Intensite de brulure", Range(0, 1)) = 0.5
		_PeakTemp ("Temperature de pointe (normalisee)", Range(0, 1)) = 0.65
		_TemperGain ("Visibilite du revenu", Range(0, 1)) = 1
		_DirPower ("Concentration directionnelle", Range(0.2, 8)) = 2
		_Spread ("Etalement le long du flux", Range(0.2, 8)) = 1.5
		_Sharpness ("Nettete des bords", Range(0.5, 8)) = 2
		_Streak ("Anisotropie (stries le long du flux)", Range(1, 16)) = 1
		_NoiseScale ("Echelle du bruit", Range(0.5, 16)) = 4
		_Pattern ("Motif (0 = taches, 1 = stries)", Range(0, 1)) = 0
		_Bleach ("Eclaircissement (0 = suie noire, 1 = trace blanche)", Range(0, 1)) = 0
		_Wrap ("Enveloppement du masque directionnel", Range(0, 2)) = 1.2
		_RingMask ("Depot lateral (vide)", Range(0, 1)) = 0
		_Matte ("Reflets eteints par le depot", Range(0, 1)) = 1

		_BurnDirW ("Direction du flux, espace monde", Vector) = (0, -1, 0, 0)
		_BurnDirO ("Direction du flux, espace objet", Vector) = (0, -1, 0, 0)
		_FlowMin ("Projection minimale du maillage sur le flux", Float) = -1
		_FlowRange ("Etendue du maillage le long du flux", Float) = 2
		_SpineAxisO ("Axe de la ligne de stagnation, espace objet", Vector) = (0, 1, 0, 0)
		_SlantAft ("Derive des stries vers l'aval", Float) = 0

		_SpotPos ("Point d'impact, espace motif (w = actif)", Vector) = (0, 0, 0, 0)
		_SpotAxis ("Sens du jet au point d'impact, espace motif", Vector) = (0, -1, 0, 0)
		_SpotRadius ("Rayon de la tache (m)", Float) = 1
		_SpotStretch ("Etirement de la tache vers l'aval", Range(1, 8)) = 1
		_SootGain ("Part de suie", Range(0, 1)) = 1
		_SpotCut ("Coupe du bord de la tache", Range(0, 1)) = 0
		_NoiseWarp ("Deformation du bruit", Range(0, 1)) = 0

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

		// --- Passe differee : decal dans le G-buffer standard d'Unity ---
		//   0 : albedo rgb + occlusion a   -> fondu alpha, occlusion conservee
		//   1 : speculaire rgb + lissage a -> multiplies : x(1 - couverture)
		//   2 : normale                     -> intacte (celle de la piece)
		//   3 : emission                    -> intacte (lumieres, chauffe)
		// Les fondus par cible demandent D3D11 ou OpenGL 4.0+.
		Pass
		{
			Name "DEFERRED"
			Tags { "LightMode" = "Deferred" }

			ZWrite Off
			Offset -1, -1
			Blend 0 SrcAlpha OneMinusSrcAlpha, Zero One
			Blend 1 DstColor Zero, Zero OneMinusSrcAlpha
			Blend 2 Off
			Blend 3 Off
			ColorMask 0 2
			ColorMask 0 3

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment fragDeferred
			#pragma target 3.0
			#pragma exclude_renderers nomrt
			#include "LosketBurnOverlay.cginc"
			ENDCG
		}

		// --- Passe avant de repli : fondu alpha sur l'image eclairee ---
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
