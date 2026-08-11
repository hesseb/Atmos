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
			#include "Assets/Post Processing/Effects/Clouds/Shader Common/CloudCommon.hlsl"
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
			float cloudStepGrowth;
			int cloudMaxSteps;
			/// 0 off, 1 step size, 2 step count, 3 segment length, 4 raw density, 5 start distance,
			/// 6 which branch of cloudShellSegment the camera is in.
			int cloudDebugMode;
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

			float cloudLightMarchLength;
			float cloudLightAbsorption;
			float cloudConeSpread;
			float cloudPowderStrength;

			float cloudPhaseForward;
			float cloudPhaseBackward;
			float cloudPhaseBlend;

			/// Schneider's cone kernel: six fixed directions the light march spreads into, scaled by
			/// step index so the samples describe a cone toward the sun rather than a line. A line
			/// march makes every cloud self-shadow as if it were a slab; the cone is what lets light
			/// wrap around a billow.
			static const float3 cloudConeKernel[6] =
			{
				float3( 0.38,  0.35,  0.86),
				float3( 0.16, -0.32,  0.94),
				float3(-0.66, -0.57,  0.49),
				float3(-0.75,  0.61,  0.26),
				float3( 0.71,  0.65, -0.26),
				float3( 0.00,  0.00,  0.00)
			};

			float cloudHg(float cosAngle, float g)
			{
				float g2 = g * g;
				return (1 - g2) / (4 * UNITY_PI * pow(max(1e-3, 1 + g2 - 2 * g * cosAngle), 1.5));
			}

			/// Two-lobe Henyey-Greenstein. One forward lobe for the bright rim when looking toward
			/// the sun, one backward for the glow when looking away. HG is already written up in the
			/// report's background section, so this term is traceable as it stands.
			///
			/// Scaled by 4*pi so that an isotropic phase evaluates to exactly 1 rather than to
			/// 1/(4*pi). Without it the whole direct term was multiplied by roughly 0.02 to 0.11 -
			/// a 10x to 50x darkening that no intensity slider in range could recover, and the
			/// reason the clouds went dark at sunset instead of orange. A phase function integrates
			/// to one over the sphere by definition; this only changes the units it is expressed in,
			/// moving the 4*pi into the term where the sun's irradiance would otherwise carry it.
			float cloudPhase(float cosAngle)
			{
				float forward = cloudHg(cosAngle, cloudPhaseForward);
				float backward = cloudHg(cosAngle, -cloudPhaseBackward);
				return lerp(forward, backward, cloudPhaseBlend) * 4 * UNITY_PI;
			}

			/// Optical depth between this point and the sun, cone-sampled.
			///
			/// Fixed at the kernel's six samples, which is Schneider's count, rather than taking a
			/// uniform step count: a runtime bound would make cloudConeKernel[i] a dynamic index
			/// into a static array, which the compiler either refuses or resolves expensively.
			/// [unroll] over a literal makes every index compile-time constant.
			float cloudLightMarch(float3 pos)
			{
				const int lightSteps = 6;
				float stepSize = cloudLightMarchLength / lightSteps;
				float totalDensity = 0;

				[unroll]
				for (int i = 0; i < lightSteps; i++)
				{
					float3 samplePos = pos + cloudSunDir * ((i + 0.5) * stepSize);
					samplePos += cloudConeKernel[i] * (cloudConeSpread * stepSize * (i + 1));
					// `cheap`: the light march skips detail erosion, where the extra fidelity is
					// invisible but the cost is the larger half of the density function.
					totalDensity += max(0, sampleCloudDensity(samplePos, true, stepSize)) * stepSize;
				}

				return totalDensity;
			}

			/// Beer with a powder term for the dark edge that comes from light having to scatter
			/// into a thin volume before it can leave it. The reference project omits this entirely
			/// and floors its transmittance with a darkness threshold instead.
			///
			/// The powder factor is NON-MONOTONIC in optical depth, and that has to be kept in
			/// check. It rises from zero, so at full strength it is darkest exactly where the light
			/// is least obstructed - the top of a cloud, which has nothing between it and the sun.
			/// At the strength this started with, the product peaked around depth 0.35 and the top
			/// of a cloud came out 0.74x the brightness of its own base: the shading was inverted,
			/// and clouds looked as dark from above as from below however high the sun was.
			///
			/// Beer has to dominate for the top-to-base gradient to point the right way. Measured
			/// over a real column - top at depth 0, base at 0.63 - the defaults now give the top
			/// 3.7x the base. Raising powder strength flattens that back out, and past about 0.45 it
			/// inverts again.
			float cloudBeerPowder(float opticalDepth)
			{
				float beer = exp(-opticalDepth * cloudLightAbsorption);
				float powder = 1 - exp(-opticalDepth * cloudLightAbsorption * 2);
				return beer * lerp(1.0, powder, cloudPowderStrength);
			}

			/// Returns the cloud on its own: rgb is what it adds, a is how much of the scene behind
			/// it survives. Kept separate from the background so this pass can run at a lower
			/// resolution than the frame and be upsampled by the composite pass below - which is
			/// the whole of the Half cost mode.
			float4 frag(v2f i) : SV_Target
			{
				float3 rayOrigin = _WorldSpaceCameraPos;
				float viewLength = length(i.viewVector);
				float3 rayDir = i.viewVector / viewLength;

				float start, end;
				if (!cloudShellSegment(rayOrigin, rayDir, start, end))
				{
					return float4(0, 0, 0, 1);
				}

				// Scene geometry ends the march. Terrain rises above the sphere the shell is built
				// on, so depth - not a ground intersection - is what actually occludes cloud.
				float sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv)) * viewLength;
				end = min(end, sceneDepth);
				if (end <= start) { return float4(0, 0, 0, 1); }

				float segment = end - start;

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
				float jitter = getBlueNoise(i.uv).r * cloudJitterStrength * stepSize;

				// Where the march begins. This is the quantity that jumps if the shell branch is at
				// fault: above the layer a shallow ray does not reach the tops for a long way, so
				// start is large; from inside, the same ray starts at zero.
				if (cloudDebugMode == 5) { return float4((start / 50.0).xxx, 0); }

				// Which region the camera is in - red above the shell, green inside, blue below.
				// If the pop coincides with a colour change here, it is the boundary crossing; if
				// the colour is constant across it, the cause is elsewhere entirely.
				if (cloudDebugMode == 6)
				{
					float camRadius = length(rayOrigin);
					if (camRadius > cloudOuterRadius) { return float4(1, 0, 0, 0); }
					if (camRadius > cloudInnerRadius) { return float4(0, 1, 0, 0); }
					return float4(0, 0, 1, 0);
				}

				if (cloudDebugMode == 1) { return float4(stepSize.xxx * 2, 0); }
				if (cloudDebugMode == 2) { return float4((steps / (float)cloudMaxSteps).xxx, 0); }
				if (cloudDebugMode == 3) { return float4((segment / 100.0).xxx, 0); }

				float phase = cloudPhase(dot(rayDir, cloudSunDir));

				float transmittance = 1;
				float3 luminance = 0;
				float rawDensity = 0;

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
					// Weighted rather than taken whole. This atmosphere is optically much thicker
					// than Earth's - zenith transmittance 0.45 against 0.77 - which is a deliberate
					// compensation for a 136 km planet, but it means even a midday sun arrives at a
					// cloud top already dimmed to 45% and reddened. Lerping toward white keeps the
					// colour physical while letting the level be authored. Same reasoning, and the
					// same shape, as the ocean glint's transmittance weight.
					float3 sunTransmittance = sampleLightColourAt(pos, cloudSunDir);
					float3 sunColour = lerp(1.0, sunTransmittance, cloudSunTransmittanceWeight) * cloudSunColour;

					float energy = cloudBeerPowder(cloudLightMarch(pos));

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

					float3 inScatter = sunColour * energy * phase * cloudSunIntensity + ambient;

					// Analytic integration over the step rather than a rectangle rule: for a purely
					// scattering medium the scattering and extinction coefficients cancel, leaving
					// exactly (1 - stepTransmittance). Converges at far lower step counts, which is
					// the same reason the atmosphere's raymarch uses this form.
					float stepTransmittance = exp(-density * stepSize * cloudExtinction);
					luminance += inScatter * (1 - stepTransmittance) * transmittance;
					transmittance *= stepTransmittance;

					if (transmittance < 0.01) { break; }
				}

				if (cloudDebugMode == 4) { return float4((rawDensity * 0.2).xxx, 0); }

				return float4(luminance, transmittance);
			}
			ENDCG
		}

		// Composite. Blends the cloud pass over the frame, upsampling it when it was rendered at a
		// lower resolution than the frame.
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

			sampler2D _MainTex;
			sampler2D _CloudTex;
			sampler2D _CameraDepthTexture;

			/// xy = cloud pass resolution, zw = its texel size.
			float4 _CloudTexSize;
			/// 1 when the cloud pass ran at full resolution, so the bilateral path is skipped.
			float _CloudUpsample;
			/// How hard to reject a neighbour across a depth discontinuity. Higher keeps silhouettes
			/// crisper and lets more of the low-resolution stair-stepping through.
			float _CloudDepthRejection;

			float sampleEyeDepth(float2 uv)
			{
				return LinearEyeDepth(tex2Dlod(_CameraDepthTexture, float4(uv, 0, 0)).r);
			}

			/// Depth-aware upsample.
			///
			/// A plain bilinear stretch of a half-resolution march bleeds cloud across every
			/// silhouette, because the four neighbours it blends may sit on opposite sides of a
			/// depth discontinuity - a mountain ridge against sky reads as a halo. Weighting each
			/// neighbour by how well its depth matches this pixel's rejects the ones that belong to
			/// different geometry.
			///
			/// The neighbours' depths are read from the full-resolution depth buffer at the
			/// low-resolution texel centres, which is exactly the depth each of those marches used -
			/// so no second depth target is needed.
			float4 upsampleCloud(float2 uv)
			{
				if (_CloudUpsample <= 1.0) { return tex2Dlod(_CloudTex, float4(uv, 0, 0)); }

				float2 coord = uv * _CloudTexSize.xy - 0.5;
				float2 base = floor(coord);
				float2 f = coord - base;

				float centreDepth = sampleEyeDepth(uv);

				float4 sum = 0;
				float weightSum = 0;

				[unroll]
				for (int y = 0; y < 2; y++)
				{
					[unroll]
					for (int x = 0; x < 2; x++)
					{
						float2 tapUv = (base + float2(x, y) + 0.5) * _CloudTexSize.zw;
						float bilinear = (x ? f.x : 1 - f.x) * (y ? f.y : 1 - f.y);
						float depthDelta = abs(sampleEyeDepth(tapUv) - centreDepth);
						float weight = bilinear / (1 + depthDelta * _CloudDepthRejection);

						sum += tex2Dlod(_CloudTex, float4(tapUv, 0, 0)) * weight;
						weightSum += weight;
					}
				}

				// Every neighbour can be rejected at once on a thin feature, so fall back to the
				// nearest tap rather than dividing by zero.
				if (weightSum < 1e-4) { return tex2Dlod(_CloudTex, float4(uv, 0, 0)); }
				return sum / weightSum;
			}

			float4 frag(v2f i) : SV_Target
			{
				float3 background = tex2D(_MainTex, i.uv).rgb;
				float4 cloud = upsampleCloud(i.uv);
				return float4(background * cloud.a + cloud.rgb, 1);
			}
			ENDCG
		}
	}
	Fallback Off
}
