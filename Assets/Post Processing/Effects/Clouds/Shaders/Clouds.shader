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
			// Sun colour after atmospheric extinction, sky radiance for a direction, the tone-map
			// constants and getBlueNoise. Deliberately NOT AtmosphereCommon, which owns the global
			// `dirToSun` and would shadow this shader's own sun direction.
			#include "Assets/Post Processing/Effects/Atmosphere/Shader Common/SurfaceLighting.hlsl"

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
				// the ray, which is what the shell segment has to be clipped against.
				float3 viewVector = mul(unity_CameraInvProjection, float4(v.uv.xy * 2 - 1, 0, -1));
				output.viewVector = mul(unity_CameraToWorld, float4(viewVector, 0));
				return output;
			}

			sampler2D _CameraDepthTexture;

			// ---- march ----------------------------------------------------------------------
			// Target length of a march step, in world units. The step COUNT is then derived from
			// the segment rather than fixed, so a vertical ray through a 1.65-unit shell and a
			// grazing ray through a 45-unit chord are both sampled properly. The reference project
			// instead hardcodes a step of 11 world units, which at this planet's scale would step
			// over the entire cloud layer six times in one step.
			float cloudStepSize;
			int cloudMaxSteps;
			/// 0 off, 3 shell length, 4 raw density, 5 start distance, 7-10 the weather channels.
			/// Camera region is handled by the composite pass instead, so the clouds stay visible
			/// beside it.
			int cloudDebugMode;

			/// Temporal reprojection. Period 1 marches every pixel; 4 marches one in four, in a 2x2
			/// pattern that cycles so every pixel is refreshed within four frames. The skip happens
			/// before the march, which is where the saving comes from - a fragment shader cannot
			/// decline to run, but it can decline to do the expensive part.
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

			/// How much aerial perspective the clouds pick up, 0 to 1.
			///
			/// Named and shaped after the atmosphere's own aerialPerspectiveStrength. At 0 the term
			/// is exactly identity rather than merely small, so the feature's cost and its visual
			/// contribution can both be priced against the same build.
			float cloudAerialStrength;

			// The optical model - phase, Beer-Powder and the cone light march - now lives in
			// CloudLightingCommon.hlsl, shared with the downward-radiance bake that lights the sea and
			// the land beneath the deck. Two consumers of the same physics have to call one definition;
			// what stays here is the ASSEMBLY, which is genuinely different between them.

			/// How many times the isotropic value the phase reaches looking straight at the sun. A call
			/// site parameter rather than part of the model, because the sun and the moon want different
			/// ones.
			float cloudSilverIntensity;

			// Published by SolarSystem.Moon, the same globals the ocean reads. A position rather
			// than a direction: at 811 world units against a 150-unit planet the moon is not far
			// enough away to be directional across the visible shell.
			float4 moonPosition;
			float4 moonLightColour;   // already scaled by phase, so a new moon contributes nothing
			float cloudMoonIntensity;
			float cloudMoonSilverIntensity;

			/// Returns the cloud on its own: rgb is what it adds, a is how much of the scene behind
			/// it survives. Kept separate from the background so this pass can run at a lower
			/// resolution than the frame and be upsampled by the composite pass below - which is
			/// the whole of the Half cost mode.
			/// Marches one span of the shell, accumulating into the caller's running totals.
			///
			/// A span rather than the whole ray, because a ray can cross the shell twice - see
			/// cloudShellSegments. Transmittance carries across both, so the far span is correctly
			/// dimmed by whatever the near one already absorbed.
			void cloudMarchSpan(
				float3 rayOrigin, float3 rayDir, float2 span, float phase, float cosViewSun, float2 uv,
				AerialRay aerialRay, float3 aerialSky,
				inout float transmittance, inout float3 luminance, inout float rawDensity)
			{
				float start = span.x;
				float end = span.y;
				float segment = end - start;
				if (segment <= 0 || transmittance < 0.01) { return; }

				// Step count from the segment, so the march ALWAYS covers the whole of it.
				//
				// A distance-keyed step was tried here and reverted: keying to distance decouples
				// the sampling rate from view direction, which is right, but it also means the
				// march can exhaust its step budget before crossing the shell, and far cloud is
				// then cut off at a hard edge - trading one discontinuity for a worse one. Covering
				// the segment is the property worth keeping; the mip selected from the step length
				// is what makes the varying rate tolerable.
				int steps = min(cloudMaxSteps, max(8, (int)(segment / max(1e-4, cloudStepSize))));
				float stepSize = segment / steps;

				// Blue-noise start offset, so undersampling breaks up into noise rather than into
				// the concentric banding a fixed phase produces on a spherical shell.
				float jitter = getBlueNoise(uv).r * cloudJitterStrength * stepSize;

				[loop]
				for (int s = 0; s < steps; s++)
				{
					float t = start + (s + 0.5) * stepSize + jitter;
					if (t >= end) { break; }

					float3 pos = rayOrigin + rayDir * t;
					float density = sampleCloudDensity(pos, false, stepSize);
					rawDensity += max(0, density) * stepSize;
					if (density <= 0) { continue; }

					// The sun's colour at THIS point's altitude, not at sea level below it - which
					// is what keeps a cloud top bright and gold while the land beneath has gone red.
					// Falls back to white when there is no physically based atmosphere bound, so the
					// clouds keep working in the baseline and ablation profiles.
					//
					// Desaturated toward its OWN luminance, not toward white.
					//
					// This atmosphere is optically much thicker than Earth's - zenith transmittance
					// 0.45 against 0.77, a deliberate compensation for a 136 km planet - so a midday
					// sun arrives already reddened, which read as drab. Pulling the colour toward
					// grey fixes that.
					//
					// Toward white it also floored the term at 1 - weight, which is 0.3 of a full
					// sun, and transmittance is exactly zero once the sun is below the horizon. So
					// the sun never actually set on the clouds: at midnight they kept 30% of a sun
					// whose gradient colour had by then gone deep orange, and they were lit as
					// though at sunset. Toward the transmittance's own luminance the term dies with
					// it, because zero has no hue to preserve.
					float3 sunTransmittance = sampleLightColourAt(pos, cloudSunDir);
					float sunLuminance = dot(sunTransmittance, float3(0.2126, 0.7152, 0.0722));
					float3 sunColour = lerp(sunLuminance.xxx, sunTransmittance, cloudSunTransmittanceWeight) * cloudSunColour;

					float energy = cloudBeerPowder(cloudLightMarch(pos, cloudSunDir), cosViewSun);

					// Skylight, from the same LUT the ocean and the land use. This is what puts the
					// sunset on the undersides, and at low sun it does more of the work than the
					// direct term does. Returns zero without an atmosphere, hence the fallback.
					//
					// Sampled toward the sunlit HORIZON as well as the zenith. At sunset the zenith
					// is deep blue while the horizon is orange, so a zenith-only lookup throws away
					// exactly the colour the clouds are supposed to be picking up - which is most of
					// why they read as dark rather than warm. Two taps, and the horizon one is what
					// carries the sunset.
					float3 up = normalize(pos);
					float3 sunTangent = cloudSunDir - up * dot(cloudSunDir, up);
					float tangentLength = length(sunTangent);
					// Degenerate with the sun overhead, and there the sky is near enough
					// azimuthally symmetric that the zenith tap alone is right anyway.
					float3 horizonDir = tangentLength > 1e-3 ? sunTangent / tangentLength : up;

					float3 skyZenith = sampleSkyViewSafe(up, up, cloudSunDir);
					float3 skyHorizon = sampleSkyViewSafe(up, horizonDir, cloudSunDir);
					float3 ambient = lerp(skyZenith, skyHorizon, cloudAmbientHorizon) * cloudAmbientIntensity;
					ambient += cloudAmbientFallback * (hasPhysicalAtmosphere() ? 0.0 : 1.0);

					// Starlight and airglow. Every source the clouds have goes to zero together once
					// the sun is down - the sky LUT is genuinely dark and the transmittance is
					// exactly zero - so without a floor they turn pure black rather than dark.
					// Declared non-physical, exactly like the ocean's and the land's night terms:
					// what it stands for is outside a single-sun scattering model rather than
					// something this renderer gets wrong.
					float nightT = saturate(dot(up, -cloudSunDir));
					ambient += cloudNightAmbient * smoothstep(0.0, 0.15, nightT);

					float3 inScatter = sunColour * energy * phase * cloudSunIntensity + ambient;

					// Moonlight, on the same machinery as the sun. Behind a branch on the moon's own
					// published colour, which is already scaled by phase - so a new moon costs
					// nothing, and neither does a moon that has set, and the branch is uniform
					// across the frame rather than varying per pixel.
					//
					// The direction is computed per sample rather than taken as a constant: at 811
					// units against a shell spanning tens of units it swings several degrees across
					// the view, which is the scale a silver lining is drawn at.
					if (dot(moonLightColour.rgb, 1) > 1e-4)
					{
						float3 moonDir = normalize(moonPosition.xyz - pos);
						float cosViewMoon = dot(rayDir, moonDir);

						// Same desaturation as the sun, and zero once the moon is below the horizon
						// for the same reason: transmittance has no hue left to preserve there.
						float3 moonT = sampleLightColourAt(pos, moonDir);
						float moonLuminance = dot(moonT, float3(0.2126, 0.7152, 0.0722));
						float3 moonColour = lerp(moonLuminance.xxx, moonT, cloudSunTransmittanceWeight) * moonLightColour.rgb;

						float moonEnergy = cloudBeerPowder(cloudLightMarch(pos, moonDir), cosViewMoon);
						float moonPhase = cloudPhase(cosViewMoon, cloudMoonSilverIntensity);

						inScatter += moonColour * moonEnergy * moonPhase * cloudMoonIntensity;
					}

					// Aerial perspective, per sample rather than once on the composited result.
					//
					// Per sample because a single representative depth cannot describe this pixel: a
					// horizon ray's cloud spans roughly 6 to 105 world units, so one depth would be
					// wrong by a factor of seventeen across the same pixel. It is also the cheaper
					// route - the march records no depth and has no spare channel to put one in,
					// which is why the temporal pass reprojects against a mid-shell sphere proxy
					// instead of a real one.
					//
					// Applied to the whole of inScatter, after the sun, ambient and moon terms: all
					// of them leave this point together and cross the same air to reach the camera.
					//
					// The in-scatter added back is the sky radiance in the view direction, weighted
					// by how much air is in the way. The exact form is L_sky(cam) - T * L_sky(pos),
					// which would cost one more tap - but sampleSkyView reads skyViewLutRadius, a
					// uniform baked at sea level, so L_sky(pos) and L_sky(cam) return the same value
					// and the difference collapses to this. Exact at both ends - no haze on a near
					// cloud, a distant one fading entirely into the sky it is seen against - and
					// approximate in between.
					float3 aerialT = aerialTransmittanceAt(aerialRay, pos);
					inScatter = lerp(inScatter,
						inScatter * aerialT + aerialSky * (1 - aerialT),
						cloudAerialStrength);

					// Analytic integration over the step rather than a rectangle rule: for a purely
					// scattering medium the scattering and extinction coefficients cancel, leaving
					// exactly (1 - stepTransmittance). Converges at far lower step counts, which is
					// the same reason the atmosphere's raymarch uses this form.
					float stepTransmittance = exp(-density * stepSize * cloudExtinction);
					luminance += inScatter * (1 - stepTransmittance) * transmittance;
					transmittance *= stepTransmittance;

					if (transmittance < 0.01) { break; }
				}
			}

			/// Returns the cloud on its own: rgb is what it adds, a is how much of the scene behind
			/// it survives. Kept separate from the background so this pass can run at a lower
			/// resolution than the frame and be upsampled by the composite pass below - which is
			/// the whole of the Half cost mode.
			float4 frag(v2f i) : SV_Target
			{
				// Alpha below zero marks a pixel this frame did not march, which the resolve pass
				// reads as "no new data, keep the history".
				if (cloudTemporalPeriod > 1)
				{
					int2 pixel = (int2)(i.uv * _CloudMarchSize.xy);
					int slot = (pixel.x & 1) + ((pixel.y & 1) << 1);
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

				// Scene geometry ends the march. Terrain rises above the sphere the shell is built
				// on, so depth - not a ground intersection - is what actually occludes cloud. This
				// is also what removes the far span when the ray runs into the planet rather than
				// skimming under the cloud base and back out.
				float sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv)) * viewLength;
				nearSpan.y = min(nearSpan.y, sceneDepth);
				farSpan.y = min(farSpan.y, sceneDepth);

				if (cloudDebugMode == 3) { return float4(((nearSpan.y - nearSpan.x + max(0, farSpan.y - farSpan.x)) / 100.0).xxx, 0); }
				if (cloudDebugMode == 5) { return float4((nearSpan.x / 50.0).xxx, 0); }

				// The weather fields, painted onto the globe by sampling where the ray first meets
				// the shell. Tuning four interacting fields without being able to look at them is
				// guesswork, and it is how a field sitting in the middle third of its range went
				// unnoticed.
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

				// Both aerial perspective terms are per PIXEL rather than per sample, and both are
				// hoisted here rather than derived inside the span - which matters, because
				// cloudMarchSpan is called twice for the same ray.
				AerialRay aerialRay = aerialRayBegin(rayOrigin, rayDir);
				float3 aerialSky = sampleSkyViewSafe(normalize(rayOrigin), rayDir, cloudSunDir);

				float transmittance = 1;
				float3 luminance = 0;
				float rawDensity = 0;

				cloudMarchSpan(rayOrigin, rayDir, nearSpan, phase, cosViewSun, i.uv,
					aerialRay, aerialSky, transmittance, luminance, rawDensity);
				cloudMarchSpan(rayOrigin, rayDir, farSpan, phase, cosViewSun, i.uv,
					aerialRay, aerialSky, transmittance, luminance, rawDensity);

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
			float4x4 _CloudPrevViewProj;
			float _CloudHistoryBlend;
			float _CloudReprojectRadius;
			float _CloudHistoryValid;

			/// Where along this ray the clouds are, near enough for reprojection.
			///
			/// The march does not record a depth, and there is no spare channel to put one in. But
			/// the clouds are confined to a shell of known radius, so intersecting a sphere at its
			/// middle gives a good proxy - far better than the scene depth, which for a sky pixel is
			/// the far plane and would reproject the cloud as though it were infinitely distant.
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
					// First frame after a resize or a mode change: nothing to reproject onto, so
					// take whatever was marched and let the pattern fill the rest in over the next
					// few frames rather than showing a hole.
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

					float2 texel = _CloudMarchSize.zw;
					float4 best = float4(0, 0, 0, 1);
					[unroll]
					for (int k = 0; k < 4; k++)
					{
						float2 offset = float2(k == 0 ? 1 : (k == 1 ? -1 : 0), k == 2 ? 1 : (k == 3 ? -1 : 0));
						float4 neighbour = tex2D(_CloudTex, i.uv + offset * texel);
						if (neighbour.a >= 0) { best = neighbour; }
					}
					return best;
				}

				// Marched this frame. Blend toward it so the sequence converges rather than
				// flickering between the pattern's four phases; drop the history entirely when it
				// reprojected off screen, because there is nothing valid to converge toward.
				if (!onScreen) { return current; }
				return lerp(history, current, _CloudHistoryBlend);
			}
			ENDCG
		}

		// Composite. Blends the cloud pass over the frame, upsampling it when it was rendered at a
		// lower resolution than the frame.
		//
		// The whole pass lives in CloudComposite.hlsl, which the BASELINE renderer includes too.
		// Shared rather than copied so the two cannot drift apart: everything that is not the
		// shading model has to be held constant, or the drift gets measured as a difference between
		// techniques.
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
