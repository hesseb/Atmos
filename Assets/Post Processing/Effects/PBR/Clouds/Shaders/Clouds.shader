Shader "Hidden/Clouds"
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
			#include "Assets/Post Processing/Effects/PBR/Clouds/Shader Common/CloudLightingCommon.hlsl"
			#include "Assets/Scripts/Shader Common/SurfaceLighting.hlsl"
			#include "Assets/Scripts/Shader Common/DrawAtmosphereCommon.hlsl"

			sampler2D _CameraDepthTexture;

			// March
			float cloudStepSize;
			int cloudMaxSteps;
			int cloudDebugMode;
			int cloudTemporalPeriod;
			int cloudTemporalIndex;
			float4 _CloudMarchSize;
			float cloudJitterStrength;

			// Lighting
			float cloudAerialStrength;

			///////// Internal helpers /////////////

			void cloudMarchSpan(
				float3 rayOrigin, float3 rayDir, float2 span, float phase, float cosViewSun, float2 uv,
				AerialRay aerialRay, float3 aerialSky, float3 skyAmbient, bool moonActive,
				inout float transmittance, inout float3 luminance, inout float opticalDepth)
			{
				float start = span.x;
				float end = span.y;
				float segment = end - start;
				if (segment <= 0 || transmittance < 0.01)
				{
					return;
				}

				int steps = min(cloudMaxSteps, max(8, (int)(segment / max(1e-4, cloudStepSize))));
				float stepSize = segment / steps;

				float jitter = lerp(0.5, getBlueNoiseRaw(uv).r, cloudJitterStrength);

				[loop]
				for (int s = 0; s < steps; s++)
				{
					float t = start + (s + jitter) * stepSize;
					if (t >= end)
					{
						break;
					}

					float3 pos = rayOrigin + rayDir * t;
					float density = sampleCloudDensity(pos, false, stepSize);
					opticalDepth += max(0, density) * stepSize;
					if (density <= 0)
					{
						continue;
					}

					float3 inScatter = cloudSunInScatter(pos, cosViewSun, phase)
						+ skyAmbient + cloudNightAmbientAt(normalize(pos));

					if (moonActive)
					{
						inScatter += cloudMoonInScatter(pos, rayDir);
					}

					float3 aerialT = aerialTransmittanceAt(aerialRay, pos);
					inScatter = lerp(inScatter,
						inScatter * aerialT + aerialSky * (1 - aerialT),
						cloudAerialStrength);

					cloudIntegrateStep(inScatter, density, stepSize, transmittance, luminance);

					// Early-out threshold from Lague's clouds; Schneider stops the same way once alpha reaches 1.
					if (transmittance < 0.01)
					{
						break;
					}
				}
			}

			///////// External entry points /////////////////

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
				float3 viewVector = mul(unity_CameraInvProjection, float4(v.uv.xy * 2 - 1, 0, -1));
				output.viewVector = mul(unity_CameraToWorld, float4(viewVector, 0));
				return output;
			}

			float4 frag(v2f i) : SV_Target
			{
				// Temporal reprojection after Schneider (SIGGRAPH 2015), who marches 1 in 16 pixels per
				// 4x4 block; here 1 in 4, in 2x2 blocks.
				if (cloudTemporalPeriod > 1)
				{
					int2 pixel = (int2)(i.uv * _CloudMarchSize.xy);

					int slot = ((pixel.x >> 1) & 1) + (((pixel.y >> 1) & 1) << 1);
					if (slot != cloudTemporalIndex)
					{
						return float4(0, 0, 0, -1);
					}
				}

				float3 rayOrigin = _WorldSpaceCameraPos;
				float viewLength = length(i.viewVector);
				float3 rayDir = i.viewVector / viewLength;

				float2 nearSpan, farSpan;
				if (!cloudShellSegments(rayOrigin, rayDir, nearSpan, farSpan))
				{
					return float4(0, 0, 0, 1);
				}

				float sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv)) * viewLength;
				nearSpan.y = min(nearSpan.y, sceneDepth);
				farSpan.y = min(farSpan.y, sceneDepth);

				if (cloudDebugMode == 3)
				{
					return float4(((max(0, nearSpan.y - nearSpan.x) + max(0, farSpan.y - farSpan.x)) / 100.0).xxx, 0);
				}
				if (cloudDebugMode == 5) {
					return float4((nearSpan.x / 50.0).xxx, 0);
				}

				if (cloudDebugMode >= 7)
				{
					float3 shellPoint = rayOrigin + rayDir * nearSpan.x;
					float4 weather = sampleCloudWeather(pointToUV(normalize(shellPoint)));
					if (cloudDebugMode == 7)
					{
						return float4(weather.rrr, 0); // coverage
					}
					if (cloudDebugMode == 8)
					{
						return float4(weather.bbb, 0); // cloud type
					}
					if (cloudDebugMode == 9)
					{
						return float4(weather.ggg, 0); // precipitation
					}
					
					return float4(weather.aaa, 0); // base height
				}

				float cosViewSun = dot(rayDir, cloudSunDir);
				float phase = cloudPhase(cosViewSun, cloudSilverIntensity);

				AerialRay aerialRay = aerialRayBegin(rayOrigin, rayDir);
				float3 aerialSky = sampleSkyViewSafe(normalize(rayOrigin), rayDir, cloudSunDir);
				float3 skyAmbient = cloudSkyAmbient(normalize(rayOrigin));

				bool moonActive = cloudMoonActive();

				float transmittance = 1;
				float3 luminance = 0;
				float opticalDepth = 0;

				cloudMarchSpan(rayOrigin, rayDir, nearSpan, phase, cosViewSun, i.uv,
					aerialRay, aerialSky, skyAmbient, moonActive, transmittance, luminance, opticalDepth);
				cloudMarchSpan(rayOrigin, rayDir, farSpan, phase, cosViewSun, i.uv,
					aerialRay, aerialSky, skyAmbient, moonActive, transmittance, luminance, opticalDepth);

				if (cloudDebugMode == 4)
				{
					return float4((opticalDepth * 0.2).xxx, 0);
				}

				return float4(luminance, transmittance);
			}
			ENDCG
		}

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag

			#include "UnityCG.cginc"

			sampler2D _CloudTex;
			sampler2D _CloudHistory;
			float4 _CloudMarchSize;
			int cloudTemporalIndex;
			float4x4 _CloudPrevViewProj;
			float _CloudHistoryBlend;
			float _CloudReprojectRadius;
			float _CloudHistoryValid;

			///////// Internal helpers /////////////

			float cloudReprojectDistance(float3 rayOrigin, float3 rayDir)
			{
				float b = dot(rayOrigin, rayDir);
				float c = dot(rayOrigin, rayOrigin) - _CloudReprojectRadius * _CloudReprojectRadius;
				float d = b * b - c;
				
				if (d < 0)
				{
					return _CloudReprojectRadius;
				}

				float s = sqrt(d);
				float near = -b - s;
				float far = -b + s;
				return near > 0 ? near : max(far, 1.0);
			}

			///////// External entry points /////////////////

			struct appdata { float4 vertex : POSITION; float4 uv : TEXCOORD0; };
			struct v2f
			{
				float4 pos : SV_POSITION;
				float2 uv : TEXCOORD0;
				float3 viewVector : TEXCOORD1;
			};

			v2f vert(appdata v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.uv = v.uv;
				float3 viewVector = mul(unity_CameraInvProjection, float4(v.uv.xy * 2 - 1, 0, -1));
				o.viewVector = mul(unity_CameraToWorld, float4(viewVector, 0));
				return o;
			}

			float4 frag(v2f i) : SV_Target
			{
				float4 current = tex2D(_CloudTex, i.uv);

				if (_CloudHistoryValid < 0.5)
				{
					return float4(current.rgb, abs(current.a));
				}

				float3 rayOrigin = _WorldSpaceCameraPos;
				float3 rayDir = normalize(i.viewVector);
				float3 worldPos = rayOrigin + rayDir * cloudReprojectDistance(rayOrigin, rayDir);

				float4 clip = mul(_CloudPrevViewProj, float4(worldPos, 1));
				float2 prevUv = (clip.xy / max(1e-6, clip.w)) * 0.5 + 0.5;
				bool onScreen = clip.w > 0 && all(prevUv > 0.0) && all(prevUv < 1.0);

				float4 history = tex2D(_CloudHistory, prevUv);

				if (current.a < 0)
				{
					if (onScreen)
					{
						return history;
					}

					int2 pixel = (int2)(i.uv * _CloudMarchSize.xy);
					int slot = ((pixel.x >> 1) & 1) + (((pixel.y >> 1) & 1) << 1);
					int diff = slot ^ cloudTemporalIndex;

					float2 offset = float2((diff & 1) ? 1 : 0, (diff & 2) ? 1 : 0) * _CloudMarchSize.zw * 2.0;
					offset = lerp(offset, -offset, step(1.0, i.uv + offset));

					return tex2D(_CloudTex, i.uv + offset);
				}

				if (!onScreen)
				{
					return current;
				}
				return lerp(history, current, _CloudHistoryBlend);
			}
			ENDCG
		}

		Pass
		{
			CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment compositeFrag

			#include "UnityCG.cginc"
			#include "Assets/Post Processing/Effects/PBR/Clouds/Shader Common/CloudComposite.hlsl"
			ENDCG
		}
	}
	Fallback Off
}
