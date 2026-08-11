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

			sampler2D _MainTex;
			sampler2D _CameraDepthTexture;

			// ---- march ----------------------------------------------------------------------
			// Target length of a march step, in world units. The step COUNT is then derived from
			// the segment rather than fixed, so a vertical ray through a 1.65-unit shell and a
			// grazing ray through a 45-unit chord are both sampled properly. The reference project
			// instead hardcodes a step of 11 world units, which at this planet's scale would step
			// over the entire cloud layer six times in one step.
			float cloudStepSize;
			int cloudMinSteps;
			int cloudMaxSteps;
			float cloudJitterStrength;
			float cloudExtinction;

			// ---- lighting -------------------------------------------------------------------
			float3 cloudSunDir;
			float3 cloudSunColour;
			float cloudSunIntensity;
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
					totalDensity += max(0, sampleCloudDensity(samplePos, true)) * stepSize;
				}

				return totalDensity;
			}

			/// Beer-Powder. Plain Beer alone makes cloud edges read as cut-outs, because the thin
			/// parts that should be brightest are exactly the parts it lightens least. The powder
			/// term reintroduces the dark edge that comes from light having to scatter into a thin
			/// volume before it can leave it. The reference project omits this entirely and floors
			/// its transmittance with a darkness threshold instead.
			float cloudBeerPowder(float opticalDepth)
			{
				float beer = exp(-opticalDepth * cloudLightAbsorption);
				float powder = 1 - exp(-opticalDepth * cloudLightAbsorption * 2);
				return 2.0 * beer * lerp(1.0, powder, cloudPowderStrength);
			}

			float4 frag(v2f i) : SV_Target
			{
				float3 background = tex2D(_MainTex, i.uv).rgb;

				float3 rayOrigin = _WorldSpaceCameraPos;
				float viewLength = length(i.viewVector);
				float3 rayDir = i.viewVector / viewLength;

				float start, end;
				if (!cloudShellSegment(rayOrigin, rayDir, start, end))
				{
					return float4(background, 1);
				}

				// Scene geometry ends the march. Terrain rises above the sphere the shell is built
				// on, so depth - not a ground intersection - is what actually occludes cloud.
				float sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv)) * viewLength;
				end = min(end, sceneDepth);
				if (end <= start) { return float4(background, 1); }

				float segment = end - start;
				int steps = clamp((int)(segment / max(1e-4, cloudStepSize)), cloudMinSteps, cloudMaxSteps);
				float stepSize = segment / steps;

				// Blue-noise start offset, so undersampling breaks up into noise rather than into
				// the concentric banding a fixed phase produces on a spherical shell.
				float jitter = getBlueNoise(i.uv).r * cloudJitterStrength * stepSize;

				float phase = cloudPhase(dot(rayDir, cloudSunDir));

				float transmittance = 1;
				float3 luminance = 0;

				for (int s = 0; s < steps; s++)
				{
					float t = start + (s + 0.5) * stepSize + jitter;
					if (t >= end) { break; }

					float3 pos = rayOrigin + rayDir * t;
					float density = sampleCloudDensity(pos, false);
					if (density <= 0) { continue; }

					// The sun's colour at THIS point's altitude, not at sea level below it - which
					// is what keeps a cloud top bright and gold while the land beneath has gone red.
					// Falls back to white when there is no physically based atmosphere bound, so the
					// clouds keep working in the baseline and ablation profiles.
					float3 sunColour = sampleLightColourAt(pos, cloudSunDir) * cloudSunColour;

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

				float3 col = background * transmittance + luminance;
				return float4(col, 1);
			}
			ENDCG
		}
	}
	Fallback Off
}
