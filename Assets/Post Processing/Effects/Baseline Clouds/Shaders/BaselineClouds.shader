// The cheap, non-physically-based baseline clouds.
//
// Deliberately a structural copy of Hidden/Clouds: the same two-pass shape, the same ray setup, the
// same depth clip, the same output convention, and literally the same composite pass - it includes
// the same header. Only the shading differs: two texture taps in place of a raymarch of up to 192
// density samples, each of which runs a six-sample cone march toward the sun.
//
// Everything that is not the shading model is held constant so the measured difference is
// attributable to the shading model. DrawSkyBaseline.shader is the precedent, and states the rule.
Shader "Hidden/BaselineClouds"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
	}
	SubShader
	{
		Cull Off ZWrite Off ZTest Always

		// Shade. Writes the cloud on its own - rgb what it adds, a what survives behind it - so this
		// pass can run at a lower resolution than the frame and be upsampled by the composite, which
		// is the whole of the Half cost mode.
		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			// Texture2D/SamplerState objects are passed to baselineCloudLayer so stage 4's second
			// layer is a second call rather than a second copy of the shading. That needs SM4.
			#pragma target 4.0

			#include "UnityCG.cginc"
			#include "Assets/Post Processing/Effects/Baseline Clouds/Shader Common/BaselineCloudCommon.hlsl"

			struct appdata
			{
				float4 vertex : POSITION;
				float4 uv : TEXCOORD0;
			};

			struct v2f
			{
				float4 pos : SV_POSITION;
				float2 uv : TEXCOORD0;
				float3 viewVector : TEXCOORD1;
			};

			v2f vert(appdata v)
			{
				v2f output;
				output.pos = UnityObjectToClipPos(v.vertex);
				output.uv = v.uv;
				// Left unnormalised on purpose: its length converts eye depth into distance along
				// the ray, which is what the layer hit has to be clipped against.
				float3 viewVector = mul(unity_CameraInvProjection, float4(v.uv.xy * 2 - 1, 0, -1));
				output.viewVector = mul(unity_CameraToWorld, float4(viewVector, 0));
				return output;
			}

			sampler2D _CameraDepthTexture;

			// The deck textures, their parameters and the two builders all live in
			// BaselineCloudCommon.hlsl, so this shader, the shadow compute and the mesh delivery
			// read the same bindings and cannot drift apart.

			/// 0 off, 1 opacity, 2 world normal, 3 mip level, 4 tint the decks apart.
			///
			/// Small on purpose - the baseline has almost no internal state to inspect, which is
			/// itself part of what RQ3 is comparing.
			int baselineCloudDebugMode;

			float4 frag(v2f i) : SV_Target
			{
				float3 rayOrigin = _WorldSpaceCameraPos;
				float viewLength = length(i.viewVector);
				float3 rayDir = i.viewVector / viewLength;

				// Terrain rises above the sphere the layer sits on, so depth - not a ground
				// intersection - is what actually occludes cloud, exactly as in the volumetric.
				float sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv)) * viewLength;

				BaselineLayer a = baselineLayerA();

				// Modes 1-3 inspect one tap of deck A and replace the frame. Mode 4 is different: it
				// leaves both decks rendering and only tints them, because the question it answers -
				// are these two sheets separating - cannot be asked of a picture with one deck in it.
				if (baselineCloudDebugMode > 0 && baselineCloudDebugMode < 4)
				{
					float t = baselineLayerHit(a.radius, rayOrigin, rayDir);
					if (t < 0 || t >= sceneDepth) { return float4(0, 0, 0, 1); }

					float3 up = normalize(rayOrigin + rayDir * t);
					float3 pTex = mul((float3x3)a.drift, up);
					float cosIncidence = abs(dot(rayDir, up));
					float lod = baselineLayerLod(a, t, cosIncidence);
					float4 s = BaselineCloudLayerA.SampleLevel(samplerBaselineCloudLayerA, pointToUV(pTex), lod);

					if (baselineCloudDebugMode == 1) { return float4(s.aaa, 0); }
					if (baselineCloudDebugMode == 2) { return float4(s.rg, 0.5, 0); }
					return float4((lod / 8.0).xxx, 0);
				}

				float distanceA;
				float4 cloud = baselineCloudLayer(
					BaselineCloudLayerA, samplerBaselineCloudLayerA, a,
					rayOrigin, rayDir, sceneDepth, distanceA);

				// Warm for the lower deck, cold for the upper. Applied to the premultiplied colour,
				// so a deck that is not drawing simply stays absent rather than tinting the sky -
				// which makes "only one deck is assigned" and "both are assigned but coincident"
				// distinguishable at a glance, and they are otherwise the same picture.
				if (baselineCloudDebugMode == 4) { cloud.rgb *= float3(1.0, 0.30, 0.25); }

				// A uniform branch, so this is genuinely free when the upper layer is off rather
				// than merely cheap - which is what makes one layer against two a measurement of
				// the depth cue rather than of a taken branch.
				if (baselineLayerCount > 1)
				{
					float distanceB;
					float4 upper = baselineCloudLayer(
						BaselineCloudLayerB, samplerBaselineCloudLayerB, baselineLayerB(),
						rayOrigin, rayDir, sceneDepth, distanceB);

					if (baselineCloudDebugMode == 4) { upper.rgb *= float3(0.25, 0.55, 1.0); }

					cloud = baselineCombineLayers(cloud, distanceA, upper, distanceB);
				}

				return cloud;
			}
			ENDCG
		}

		// Composite. The SAME code the volumetric composites with - not a copy of it - so the two
		// renderers differ only in the pass above and the Full/Half cost modes mean the same thing
		// for both.
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
