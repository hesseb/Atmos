// A sky pass that renders no sky.
//
// This is a measurement control, not a renderer. It has the same pass structure as every
// other sky - same command buffer slot, same temporary target, same two blits - but its
// fragment shader only copies. Subtracting it from a real sky isolates the cost of the
// shading model from the cost of the pass that carries it:
//
//   nullsky - noatmo    = the pass structure alone (two full-screen blits, temp RT, resolve)
//   baseline - nullsky  = the cheap shading model
//   pbr - nullsky       = raymarched scattering + aerial perspective + LUT computes
//
// Without this arm those three costs are indistinguishable, and at 2560x1440 the structural
// term is not small relative to the effect being measured.
Shader "Hidden/DrawSkyNull"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
	}
	SubShader
	{
		Cull Off ZWrite Off ZTest Always

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag

			#include "UnityCG.cginc"

			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct v2f
			{
				float2 uv : TEXCOORD0;
				float4 vertex : SV_POSITION;
			};

			v2f vert (appdata v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.uv = v.uv;
				return o;
			}

			sampler2D _MainTex;

			float4 frag (v2f i) : SV_Target
			{
				// Deliberately the whole shader.
				return tex2D(_MainTex, i.uv);
			}
			ENDCG
		}
	}
}
