// The baseline clouds delivered as drawn geometry rather than as a post-process: same shading model
// (shares BaselineCloudCommon.hlsl and calls the same baselineCloudLayer), reached by rasterising a
// sphere at the deck's radius so only pixels the deck covers ever run a fragment - the cost trade-off
// against the post-process's full-screen quad is the point of the comparison.
//
// The mesh is only a rasterisation proxy: the fragment still intersects the analytic sphere, so
// faceting only decides which pixels get shaded. A coarse icosphere suffices, inflated slightly so
// its inscribed silhouette doesn't clip a thin crescent off the limb.
Shader "Hidden/BaselineCloudShell"
{
	Properties
	{
		// Set per frame: Back when the camera is outside the deck, Front when inside.
		_Cull ("Cull", Float) = 2
	}
	SubShader
	{
		Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

		Pass
		{
			Cull [_Cull]
			ZWrite Off
			// Premultiplied colour with transmittance in alpha, so src + dst*srcA matches exactly what
			// CloudComposite.hlsl does for the post-process path - the two deliveries must not differ in compositing.
			Blend One SrcAlpha

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 4.0

			#include "UnityCG.cginc"
			#include "Assets/Post Processing/Effects/Baseline Clouds/Shader Common/BaselineCloudCommon.hlsl"

			struct appdata
			{
				float4 vertex : POSITION;
			};

			struct v2f
			{
				float4 pos : SV_POSITION;
				float3 worldPos : TEXCOORD0;
				float4 screenPos : TEXCOORD1;
			};

			v2f vert(appdata v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
				o.screenPos = ComputeScreenPos(o.pos);
				return o;
			}

			sampler2D _CameraDepthTexture;

			/// 0 draws the lower deck, 1 the upper. One shell object per deck, so this is uniform
			/// across the draw and the branch is free.
			float baselineShellIsUpper;

			float4 frag(v2f i) : SV_Target
			{
				float3 rayOrigin = _WorldSpaceCameraPos;
				float3 rayDir = normalize(i.worldPos - rayOrigin);

				// Scene depth, converted from eye-space Z into distance along this (normalised) ray by
				// dividing by its cosine against the camera's forward.
				float2 screenUV = i.screenPos.xy / max(1e-6, i.screenPos.w);
				float eyeDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, screenUV));
				float3 cameraForward = -UNITY_MATRIX_V[2].xyz;
				float sceneDepth = eyeDepth / max(1e-4, dot(rayDir, cameraForward));

				float hitDistance;
				float4 cloud = baselineShellIsUpper > 0.5
					? baselineCloudLayer(BaselineCloudLayerB, samplerBaselineCloudLayerB,
						baselineLayerB(), rayOrigin, rayDir, sceneDepth, hitDistance)
					: baselineCloudLayer(BaselineCloudLayerA, samplerBaselineCloudLayerA,
						baselineLayerA(), rayOrigin, rayDir, sceneDepth, hitDistance);

				return cloud;
			}
			ENDCG
		}
	}
	Fallback Off
}
