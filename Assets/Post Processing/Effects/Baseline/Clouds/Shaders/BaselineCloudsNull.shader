// The measurement control for the cloud pass: structurally identical to Hidden/BaselineClouds (same
// passes, target, composite) but the first pass returns "no cloud" without shading anything, so
// pass-structure cost can be subtracted from shading cost:
//
//   baseline-null  - clouds-off     the pass structure alone
//   baseline       - baseline-null  the cheap shading model
//   volumetric     - baseline-null  the march
Shader "Hidden/BaselineCloudsNull"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
	}
	SubShader
	{
		Cull Off ZWrite Off ZTest Always

		// Shade - or rather, decline to: rgb 0 is "adds nothing", alpha 1 is "background survives".
		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag

			#include "UnityCG.cginc"

			struct appdata
			{
				float4 vertex : POSITION;
				float4 uv : TEXCOORD0;
			};

			struct v2f
			{
				float4 pos : SV_POSITION;
				float2 uv : TEXCOORD0;
			};

			v2f vert(appdata v)
			{
				v2f output;
				output.pos = UnityObjectToClipPos(v.vertex);
				output.uv = v.uv;
				return output;
			}

			float4 frag(v2f i) : SV_Target
			{
				return float4(0, 0, 0, 1);
			}
			ENDCG
		}

		// The same composite the shaded path and the volumetric both use - stubbing it out would move
		// its cost from "structure" into "shading", the one boundary this control exists to draw.
		Pass
		{
			CGPROGRAM
			#pragma vertex compositeVert
			#pragma fragment compositeFrag

			#include "UnityCG.cginc"
			#include "Assets/Post Processing/Effects/PBR/Clouds/Shader Common/CloudComposite.hlsl"
			ENDCG
		}
	}
	Fallback Off
}
