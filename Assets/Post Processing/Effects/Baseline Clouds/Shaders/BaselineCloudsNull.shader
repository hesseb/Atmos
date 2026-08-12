// The measurement control for the cloud pass: its structure with no shading at all.
//
// Structurally identical to Hidden/BaselineClouds - the same two passes, the same offscreen
// ARGBHalf target allocated by the same code, the same composite. The only difference is that the
// first pass returns "no cloud, background fully intact" without reading a texture, intersecting a
// sphere or lighting anything.
//
// DrawSkyNull.shader exists for exactly this reason on the sky side, and states the argument: some
// of what a pass costs is not shading. A render-target allocation, two full-screen blits, the
// bandwidth of writing and then reading a half-precision buffer, and the composite's four depth
// taps are all paid before a single cloud is evaluated. Subtracting them is what turns a frame time
// into a statement about a technique:
//
//   baseline-null  - clouds-off     the pass structure alone
//   baseline       - baseline-null  the cheap shading model
//   volumetric     - baseline-null  the march
//
// Without this arm, "the baseline costs X" silently includes the scaffolding that any cloud
// renderer in this chain would pay, and the ratio against the volumetric is understated.
Shader "Hidden/BaselineCloudsNull"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
	}
	SubShader
	{
		Cull Off ZWrite Off ZTest Always

		// Shade - or rather, decline to. Returns the identity for the compositing convention: rgb 0
		// is "adds nothing", alpha 1 is "all of the background survives".
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

		// The SAME composite the shaded path and the volumetric both use. Included rather than
		// stubbed out, because the composite is part of the structure being priced - replacing it
		// with a blit here would move its cost out of "structure" and into "shading", which is the
		// one boundary this control exists to draw.
		Pass
		{
			CGPROGRAM
			#pragma vertex compositeVert
			#pragma fragment compositeFrag

			#include "UnityCG.cginc"
			#include "Assets/Post Processing/Effects/Clouds/Shader Common/CloudComposite.hlsl"
			ENDCG
		}
	}
	Fallback Off
}
