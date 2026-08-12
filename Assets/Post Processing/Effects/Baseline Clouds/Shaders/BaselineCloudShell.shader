// The baseline clouds delivered as DRAWN GEOMETRY rather than as a post-process.
//
// Same shading model, reached differently. BaselineClouds.shader rasterises a full-screen quad and
// intersects the deck analytically for every pixel on screen; this rasterises a sphere at the deck's
// radius so only the pixels the deck actually covers ever run a fragment. Which of those is cheaper
// is not obvious in advance - the post-process pays for sky pixels it will discard, the mesh pays
// for vertex work and a transparent pass with no depth prepass - and measuring the gap is the point.
//
// It includes BaselineCloudCommon.hlsl and calls the SAME baselineCloudLayer. Nothing about the
// look is restated here, so a visible difference between the two deliveries means the header is not
// actually shared, which is the one thing this pairing is meant to prove.
//
// The mesh is only a rasterisation proxy. The fragment still intersects the analytic sphere, so
// faceting does not affect the shading at all - it decides which pixels get shaded and nothing else.
// That is why a fairly coarse icosphere is enough, and why the shell is inflated very slightly: an
// inscribed polyhedron's silhouette sits inside the true sphere and would clip a thin crescent off
// the limb.
Shader "Hidden/BaselineCloudShell"
{
	Properties
	{
		// Set per frame from BaselineCloudShell. Back when the camera is outside the deck, Front
		// when it is inside - which is the geometric counterpart of baselineLayerHit taking the
		// near hit from outside and the exit hit from inside.
		_Cull ("Cull", Float) = 2
	}
	SubShader
	{
		Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

		Pass
		{
			Cull [_Cull]
			ZWrite Off
			// The shading returns premultiplied colour with TRANSMITTANCE in alpha - rgb is what the
			// deck adds, a is how much of the background survives. So the blend is src + dst*srcA,
			// which is exactly the expression CloudComposite.hlsl applies for the post-process path.
			// Getting this wrong would make the two deliveries differ in compositing rather than in
			// delivery, which is the confound this whole pairing exists to avoid.
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

				// Scene depth, converted from eye-space Z into distance along this ray. The ray is
				// normalised, so the eye depth has to be divided by its cosine against the camera's
				// forward - the post-process gets the same conversion for free by leaving its view
				// vector unnormalised.
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
