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
			#include "Assets/Post Processing/Effects/Clouds/Shader Common/CloudLightingCommon.hlsl"
			// Sun transmittance, sky radiance and tone-map constants. Not AtmosphereCommon, which
			// owns the global `dirToSun` and would shadow this shader's own sun direction.
			#include "Assets/Scripts/Shader Common/SurfaceLighting.hlsl"
			#include "Assets/Scripts/Shader Common/DrawAtmosphereCommon.hlsl"  // getBlueNoise

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
				// Left unnormalised: its length converts eye depth into distance along the ray.
				float3 viewVector = mul(unity_CameraInvProjection, float4(v.uv.xy * 2 - 1, 0, -1));
				output.viewVector = mul(unity_CameraToWorld, float4(viewVector, 0));
				return output;
			}

			sampler2D _CameraDepthTexture;

			// ---- march ----------------------------------------------------------------------
			// Target length of a march step, in world units; step COUNT is derived from the segment
			// rather than fixed, so both a near-vertical ray and a grazing one are sampled properly.
			float cloudStepSize;
			int cloudMaxSteps;
			/// 0 off, 3 shell length, 4 raw density, 5 start distance, 7-10 the weather channels.
			int cloudDebugMode;

			/// Temporal reprojection: period 1 marches every pixel, 4 marches one in four in a cycling
			/// 2x2 pattern. The skip happens before the march, which is where the saving comes from.
			int cloudTemporalPeriod;
			int cloudTemporalIndex;
			float4 _CloudMarchSize;
			float cloudJitterStrength;
			float cloudExtinction;

			// ---- lighting -------------------------------------------------------------------
			float3 cloudSunDir;
			float3 cloudSunColour;
			float cloudSunIntensity;
			float cloudSunTransmittanceWeight;
			float cloudAmbientIntensity;
			float cloudAmbientHorizon;
			float3 cloudAmbientFallback;
			float3 cloudNightAmbient;

			/// How much aerial perspective the clouds pick up, 0 to 1. At 0 the term is exactly
			/// identity, so cost and visual contribution can both be priced against the same build.
			float cloudAerialStrength;

			// The optical model (phase, Beer-Powder, cone light march) lives in CloudLightingCommon.hlsl,
			// shared with the downward-radiance bake; what stays here is the assembly, which differs between them.

			/// Silver-lining intensity at the sun; passed per call site since the sun and moon want different ones.
			float cloudSilverIntensity;

			// Published by SolarSystem.Moon, the same globals the ocean reads. A position rather than
			// a direction, since the moon isn't far enough away to be directional across the shell.
			float4 moonPosition;
			float4 moonLightColour;   // already scaled by phase, so a new moon contributes nothing
			float cloudMoonIntensity;
			float cloudMoonSilverIntensity;

			/// Skylight reaching a cloud sample: zenith and sunlit horizon, blended, so a sunset's
			/// orange horizon isn't lost to a zenith-only lookup. Evaluated once per pixel rather than
			/// per sample - both taps are exactly invariant along the ray, independent of `up` - which
			/// is the same hoist CloudSkyMap.compute applies inside its own loop. Not an approximation:
			/// sampleSkyView already has no positional dependence, reading skyViewLutRadius at sea level.
			float3 cloudSkyAmbient(float3 up)
			{
				float3 sunTangent = cloudSunDir - up * dot(cloudSunDir, up);
				float tangentLength = length(sunTangent);
				// Degenerate with the sun overhead, where the zenith tap alone is right anyway.
				float3 horizonDir = tangentLength > 1e-3 ? sunTangent / tangentLength : up;

				float3 skyZenith = sampleSkyViewSafe(up, up, cloudSunDir);
				float3 skyHorizon = sampleSkyViewSafe(up, horizonDir, cloudSunDir);

				float3 ambient = lerp(skyZenith, skyHorizon, cloudAmbientHorizon) * cloudAmbientIntensity;
				// Returns zero without an atmosphere, hence the fallback.
				return ambient + cloudAmbientFallback * (hasPhysicalAtmosphere() ? 0.0 : 1.0);
			}

			/// Marches one span of the shell, accumulating into the caller's running totals. A span
			/// rather than the whole ray since a ray can cross the shell twice (cloudShellSegments);
			/// transmittance carries across both spans.
			void cloudMarchSpan(
				float3 rayOrigin, float3 rayDir, float2 span, float phase, float cosViewSun, float2 uv,
				AerialRay aerialRay, float3 aerialSky, float3 skyAmbient,
				inout float transmittance, inout float3 luminance, inout float rawDensity)
			{
				float start = span.x;
				float end = span.y;
				float segment = end - start;
				if (segment <= 0 || transmittance < 0.01) { return; }

				// Step count from the segment, so the march always covers the whole of it. A
				// distance-keyed step was tried and reverted: it can exhaust its budget before
				// crossing the shell, cutting far cloud off at a hard edge.
				int steps = min(cloudMaxSteps, max(8, (int)(segment / max(1e-4, cloudStepSize))));
				float stepSize = segment / steps;

				// Blue-noise start offset so undersampling breaks into noise rather than concentric
				// banding. Uses getBlueNoiseRaw (not the dither transform, which is mean-shifted off
				// 0.5 and would make the march fall short of its own span). lerp from 0.5 rather than
				// scaling, so the mean stays on the midpoint rule at every strength.
				float jitter = lerp(0.5, getBlueNoiseRaw(uv).r, cloudJitterStrength);

				[loop]
				for (int s = 0; s < steps; s++)
				{
					float t = start + (s + jitter) * stepSize;
					if (t >= end) { break; }

					float3 pos = rayOrigin + rayDir * t;
					float density = sampleCloudDensity(pos, false, stepSize);
					rawDensity += max(0, density) * stepSize;
					if (density <= 0) { continue; }

					// The sun's colour at this point's altitude, not at sea level - keeps a cloud top
					// gold while the land below has gone red. Falls back to white without a physical
					// atmosphere. Desaturated toward its own luminance rather than toward white, so
					// the term genuinely dies with the transmittance instead of flooring at a fixed
					// fraction of a full sun (which kept clouds lit after the sun had set).
					float3 sunTransmittance = sampleLightColourAt(pos, cloudSunDir);
					float sunLuminance = dot(sunTransmittance, float3(0.2126, 0.7152, 0.0722));
					float3 sunColour = lerp(sunLuminance.xxx, sunTransmittance, cloudSunTransmittanceWeight) * cloudSunColour;

					float energy = cloudBeerPowder(cloudLightMarch(pos, cloudSunDir), cosViewSun);

					// Skylight, hoisted to the pixel - see cloudSkyAmbient.
					float3 ambient = skyAmbient;

					// The one part that genuinely varies per-sample: how far past the terminator this sample sits.
					float3 up = normalize(pos);

					// Starlight and airglow: a non-physical floor (like the ocean's and land's night
					// terms) so clouds go dark rather than pure black once every real source is zero.
					float nightT = saturate(dot(up, -cloudSunDir));
					ambient += cloudNightAmbient * smoothstep(0.0, 0.15, nightT);

					float3 inScatter = sunColour * energy * phase * cloudSunIntensity + ambient;

					// Moonlight, on the same machinery as the sun, gated on the moon's own published
					// colour (already phase-scaled, so a new or set moon costs nothing). Direction is
					// computed per sample since it swings several degrees across the view.
					if (dot(moonLightColour.rgb, 1) > 1e-4)
					{
						float3 moonDir = normalize(moonPosition.xyz - pos);
						float cosViewMoon = dot(rayDir, moonDir);

						// Same desaturation as the sun.
						float3 moonT = sampleLightColourAt(pos, moonDir);
						float moonLuminance = dot(moonT, float3(0.2126, 0.7152, 0.0722));
						float3 moonColour = lerp(moonLuminance.xxx, moonT, cloudSunTransmittanceWeight) * moonLightColour.rgb;

						float moonEnergy = cloudBeerPowder(cloudLightMarch(pos, moonDir), cosViewMoon);
						float moonPhase = cloudPhase(cosViewMoon, cloudMoonSilverIntensity);

						inScatter += moonColour * moonEnergy * moonPhase * cloudMoonIntensity;
					}

					// Aerial perspective, per sample rather than on the composited result - a single
					// representative depth can't describe a horizon ray's cloud span. Applied to the
					// whole of inScatter, since sun/ambient/moon all cross the same air to the camera.
					// The in-scatter term is sky radiance weighted by air in the way; the exact form
					// L_sky(cam) - T*L_sky(pos) collapses to this because sampleSkyView has no
					// positional dependence (skyViewLutRadius is baked at sea level).
					float3 aerialT = aerialTransmittanceAt(aerialRay, pos);
					inScatter = lerp(inScatter,
						inScatter * aerialT + aerialSky * (1 - aerialT),
						cloudAerialStrength);

					// Analytic per-step integration: for a purely scattering medium this is exactly
					// (1 - stepTransmittance), the same form the atmosphere's raymarch uses.
					float stepTransmittance = exp(-density * stepSize * cloudExtinction);
					luminance += inScatter * (1 - stepTransmittance) * transmittance;
					transmittance *= stepTransmittance;

					if (transmittance < 0.01) { break; }
				}
			}

			/// Returns the cloud on its own: rgb is what it adds, a is how much of the scene behind it
			/// survives. Kept separate from the background so this pass can run at a lower resolution
			/// and be upsampled by the composite pass - the whole of the Half cost mode.
			float4 frag(v2f i) : SV_Target
			{
				// Alpha below zero marks a pixel this frame did not march, which the resolve pass
				// reads as "no new data, keep the history".
				if (cloudTemporalPeriod > 1)
				{
					int2 pixel = (int2)(i.uv * _CloudMarchSize.xy);

					// Slot comes from bit 1 of each axis, not bit 0, so the pattern is 2x2 blocks
					// rather than individual pixels. A GPU shades in 2x2 quads and a wave costs what
					// its slowest lane costs; keying on bit 0 puts a marching pixel in every quad and
					// the early-out saves nothing. Keying on bit 1 gives each quad a uniform slot, so
					// three quads in four skip entirely and the wave genuinely retires early.
					int slot = ((pixel.x >> 1) & 1) + (((pixel.y >> 1) & 1) << 1);
					if (slot != cloudTemporalIndex) { return float4(0, 0, 0, -1); }
				}

				float3 rayOrigin = _WorldSpaceCameraPos;
				float viewLength = length(i.viewVector);
				float3 rayDir = i.viewVector / viewLength;

				float2 nearSpan, farSpan;
				if (!cloudShellSegments(rayOrigin, rayDir, nearSpan, farSpan))
				{
					return float4(0, 0, 0, 1);
				}

				// Scene geometry ends the march: terrain rises above the sphere the shell is built on,
				// so depth (not a sphere intersection) is what actually occludes cloud.
				float sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv)) * viewLength;
				nearSpan.y = min(nearSpan.y, sceneDepth);
				farSpan.y = min(farSpan.y, sceneDepth);

				if (cloudDebugMode == 3) { return float4(((nearSpan.y - nearSpan.x + max(0, farSpan.y - farSpan.x)) / 100.0).xxx, 0); }
				if (cloudDebugMode == 5) { return float4((nearSpan.x / 50.0).xxx, 0); }

				// The weather fields, painted onto the globe by sampling where the ray first meets the shell.
				if (cloudDebugMode >= 7)
				{
					float3 shellPoint = rayOrigin + rayDir * nearSpan.x;
					float4 weather = sampleCloudWeather(shellPoint);
					if (cloudDebugMode == 7) { return float4(weather.rrr, 0); }   // coverage
					if (cloudDebugMode == 8) { return float4(weather.bbb, 0); }   // cloud type
					if (cloudDebugMode == 9) { return float4(weather.ggg, 0); }   // precipitation
					return float4(weather.aaa, 0);                                // base height
				}

				float cosViewSun = dot(rayDir, cloudSunDir);
				float phase = cloudPhase(cosViewSun, cloudSilverIntensity);

				// Per-pixel terms, hoisted here rather than derived inside the span since
				// cloudMarchSpan is called twice for the same ray.
				AerialRay aerialRay = aerialRayBegin(rayOrigin, rayDir);
				float3 aerialSky = sampleSkyViewSafe(normalize(rayOrigin), rayDir, cloudSunDir);
				float3 skyAmbient = cloudSkyAmbient(normalize(rayOrigin));

				float transmittance = 1;
				float3 luminance = 0;
				float rawDensity = 0;

				cloudMarchSpan(rayOrigin, rayDir, nearSpan, phase, cosViewSun, i.uv,
					aerialRay, aerialSky, skyAmbient, transmittance, luminance, rawDensity);
				cloudMarchSpan(rayOrigin, rayDir, farSpan, phase, cosViewSun, i.uv,
					aerialRay, aerialSky, skyAmbient, transmittance, luminance, rawDensity);

				if (cloudDebugMode == 4) { return float4((rawDensity * 0.2).xxx, 0); }

				return float4(luminance, transmittance);
			}
			ENDCG
		}

		// Temporal resolve. Fills the pixels this frame did not march from the previous frame's
		// result, reprojected to account for camera motion.
		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag

			#include "UnityCG.cginc"

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

			sampler2D _CloudTex;
			sampler2D _CloudHistory;
			float4 _CloudMarchSize;
			// The slot marched this frame, set on the material so the fallback below can compute
			// where the marched block is rather than searching for it.
			int cloudTemporalIndex;
			float4x4 _CloudPrevViewProj;
			float _CloudHistoryBlend;
			float _CloudReprojectRadius;
			float _CloudHistoryValid;

			/// Where along this ray the clouds are, near enough for reprojection. The march records no
			/// depth, so a sphere at the shell's middle radius stands in - better than scene depth,
			/// which for a sky pixel is the far plane.
			float cloudReprojectDistance(float3 rayOrigin, float3 rayDir)
			{
				float b = dot(rayOrigin, rayDir);
				float c = dot(rayOrigin, rayOrigin) - _CloudReprojectRadius * _CloudReprojectRadius;
				float d = b * b - c;
				if (d < 0) { return _CloudReprojectRadius; }   // misses; any finite distance will do
				float s = sqrt(d);
				float near = -b - s;
				float far = -b + s;
				return near > 0 ? near : max(far, 1.0);
			}

			float4 frag(v2f i) : SV_Target
			{
				float4 current = tex2D(_CloudTex, i.uv);

				if (_CloudHistoryValid < 0.5)
				{
					// First frame after a resize or mode change: nothing to reproject, so take
					// whatever was marched and let the pattern fill in over the next few frames.
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
					// Not marched this frame. Reproject if the history has it; otherwise fall back
					// to a marched neighbour, since a hole would read as a hard dot pattern.
					if (onScreen) { return history; }

					// The marched block sits at a known offset (one tap, not a search): xoring this
					// block's slot against the marched one names the axes to step along directly,
					// rather than a fixed cross-pattern that misses the diagonal block.
					int2 pixel = (int2)(i.uv * _CloudMarchSize.xy);
					int slot = ((pixel.x >> 1) & 1) + (((pixel.y >> 1) & 1) << 1);
					int diff = slot ^ cloudTemporalIndex;

					float2 offset = float2((diff & 1) ? 1 : 0, (diff & 2) ? 1 : 0) * _CloudMarchSize.zw * 2.0;
					// Step backwards on any axis where stepping forwards would leave the texture, so
					// the edge of the frame does not reintroduce the hole this just removed.
					offset = lerp(offset, -offset, step(1.0, i.uv + offset));

					return tex2D(_CloudTex, i.uv + offset);
				}

				// Marched this frame: blend toward it so the sequence converges rather than
				// flickering between phases; drop the history if it reprojected off screen.
				if (!onScreen) { return current; }
				return lerp(history, current, _CloudHistoryBlend);
			}
			ENDCG
		}

		// Composite: blends the cloud pass over the frame, upsampling if rendered at lower resolution.
		// Lives in CloudComposite.hlsl, shared with the baseline renderer so the two can't drift apart.
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
