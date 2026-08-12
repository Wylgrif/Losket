// Shader temoin : sert uniquement a valider la chaine Unity -> .shaderbundle -> KSP.
// Il sera remplace par les vrais shaders de brulure/poussiere.
Shader "Losket/Probe"
{
	Properties
	{
		_Color ("Couleur", Color) = (1, 0, 1, 1)
	}

	SubShader
	{
		Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"

			fixed4 _Color;

			struct appdata
			{
				float4 vertex : POSITION;
				float3 normal : NORMAL;
			};

			struct v2f
			{
				float4 pos : SV_POSITION;
				float3 worldNormal : TEXCOORD0;
			};

			v2f vert(appdata v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.worldNormal = UnityObjectToWorldNormal(v.normal);
				return o;
			}

			fixed4 frag(v2f i) : SV_Target
			{
				// Eclairage lambertien minimal, juste pour que la forme soit lisible.
				fixed ndotl = saturate(dot(normalize(i.worldNormal), float3(0, 1, 0))) * 0.5 + 0.5;
				return fixed4(_Color.rgb * ndotl, _Color.a);
			}
			ENDCG
		}
	}

	FallBack "Diffuse"
}
