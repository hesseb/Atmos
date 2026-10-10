Shader "Hidden/Atmosphere"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
	}
	SubShader
	{
		// No culling or depth
		Cull Off ZWrite Off ZTest Always

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag

			#include "UnityCG.cginc"
			#include "Assets/Scripts/Shader Common/AtmosphereCommon.hlsl"
			#include "Assets/Scripts/Shader Common/DrawAtmosphereCommon.hlsl"

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

			v2f vert (appdata v)
			{
					v2f output;
					output.pos = UnityObjectToClipPos(v.vertex);
					output.uv = v.uv;
					float3 viewVector = mul(unity_CameraInvProjection, float4(v.uv.xy * 2 - 1, 0, -1));
					output.viewVector = mul(unity_CameraToWorld, float4(viewVector,0));
					return output;
			}

			sampler2D _MainTex;
			sampler2D _CameraDepthTexture;

			sampler3D AerialLuminanceLUT;
			sampler3D AerialTransmittanceLUT;

			float aerialPerspectiveStrength;

			/// 0 off, 1 the light-shaft occlusion term. See AtmosphereEffect.DebugMode.
			int atmosphereDebugMode;

			/// The cloud occlusion the in-scatter march sees, averaged along this view ray.
			float shaftVisibilityAlong(float3 rayOrigin, float3 rayDir, float sceneDepth)
			{
				SphereChord hitInfo = raySphereChordAtOrigin(atmosphereRadius, rayOrigin, rayDir);
				float3 inPoint = rayOrigin + rayDir * hitInfo.dstTo;

				float span = min(hitInfo.dstThrough, sceneDepth - hitInfo.dstTo);
				float dstToGround = rayIntersectSphereAtOrigin(inPoint, rayDir, planetRadius);
				if (dstToGround > 0)
				{
					span = min(span, dstToGround);
				}
				if (span <= 0)
				{
					return 1;
				}

				const int steps = 12;
				float total = 0;
				for (int i = 0; i < steps; i++)
				{
					float3 p = inPoint + rayDir * (span * (i + 0.5) / steps);
					total += cloudSunVisibility(p, dirToSun);
				}
				return total / steps;
			}

			float3 getAtmoCol(float2 uv, float3 originalCol, float viewLength, float3 viewDir)
			{
				float3 outputCol = originalCol;
				float nonlin_depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);

				float eyeDepth = LinearEyeDepth(nonlin_depth);
				float sceneDepth = eyeDepth * viewLength;

				float farClipPlane = _ProjectionParams.z;

				float3 rayOrigin = _WorldSpaceCameraPos;
				float3 rayDir = viewDir;


				SphereChord hitInfo = raySphereChordAtOrigin(atmosphereRadius, rayOrigin, rayDir);
				float dstToAtmosphere = hitInfo.dstTo;
				float dstThroughAtmosphere = hitInfo.dstThrough;

				// Sky is decided on eye depth, not on the radial distance, and with a tolerance.
				bool isSky = eyeDepth >= farClipPlane * 0.999;

				// View ray goes through atmosphere (and not blocked by anything in front of it)
				if (!isSky && dstThroughAtmosphere > 0 && dstToAtmosphere < sceneDepth)
				{
					float3 inPoint = rayOrigin + rayDir * (dstToAtmosphere);
					float3 outPoint = rayOrigin + rayDir * min(dstToAtmosphere + dstThroughAtmosphere, sceneDepth);

					// The depth axis spans the air in front of THIS ray, not a fixed distance.
					float dstToGround = rayIntersectSphereAtOrigin(inPoint, rayDir, planetRadius);
					float span = dstToGround > 0 ? min(dstThroughAtmosphere, dstToGround) : dstThroughAtmosphere;

					float horizonDst = sqrt(max(0, dot(inPoint, inPoint) - planetRadius * planetRadius));
					span = min(span, horizonDst);

					float depthT = saturate((sceneDepth - dstToAtmosphere) / max(1e-5, span));

					float3 transmittance = tex3Dlod(AerialTransmittanceLUT, float4(uv, depthT, 0)).rgb;
					float3 luminance = tex3Dlod(AerialLuminanceLUT, float4(uv,depthT, 0)).rgb;

					luminance = toneMap(luminance);
					luminance = originalCol.rgb * transmittance + luminance;

					outputCol = blueNoiseDither(luminance, uv, ditherStrength);
					outputCol = lerp(originalCol.rgb, outputCol, aerialPerspectiveStrength);

				}

				return outputCol;
			}

			float4 frag (v2f i) : SV_Target
			{
				float4 originalCol = tex2D(_MainTex, i.uv);

				float viewLength = length(i.viewVector);
				float3 viewDir = i.viewVector / viewLength;

				if (atmosphereDebugMode == 1)
				{
					float nonlin = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
					float sceneDepth = LinearEyeDepth(nonlin) * viewLength;
					float vis = shaftVisibilityAlong(_WorldSpaceCameraPos, viewDir, sceneDepth);
					return float4(vis.xxx, 1);
				}

				float3 c = getAtmoCol(i.uv, originalCol, viewLength, viewDir);

				return float4(c, 1);
			}
			ENDCG
		}
	}
}
