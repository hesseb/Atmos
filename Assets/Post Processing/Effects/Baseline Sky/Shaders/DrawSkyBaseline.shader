// The cheap, non-physically-based baseline sky.
//
// Deliberately a structural copy of Hidden/DrawSky: same pass shape, same _MainTex read-back,
// same star/moon alpha composite, same tone map and dither. Only the radiance computation
// differs - a texture lookup and a few ALU terms in place of a 256-step raymarch through a
// precomputed scattering LUT. Everything that is not the shading model is held constant so
// the measured difference is attributable to the shading model.
//
// Two texture variants, selected by keyword:
//   SKY_GRADIENT - a 2D LUT indexed by (view elevation, sun elevation). One fetch, responds
//                  to time of day, and hand-editable in any image editor.
//   SKY_CUBEMAP  - a single static cubemap. Cheapest possible, but cannot respond to the sun
//                  moving, which the day/night cycle makes obvious.
//
// The gradient LUT stores LINEAR radiance, not tone-mapped colour, so it goes through the
// same toneMap() as the physically based sky. Storing tone-mapped colour would have made the
// comparison confound the sky model with the tone-mapping stage.
Shader "Hidden/DrawSkyBaseline"
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
			#pragma multi_compile SKY_GRADIENT SKY_CUBEMAP

			#include "UnityCG.cginc"
			#include "../../Atmosphere/Shader Common/DrawAtmosphereCommon.hlsl"

			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct v2f
			{
				float2 uv : TEXCOORD0;
				float4 vertex : SV_POSITION;
				float3 viewVector : TEXCOORD1;
			};

			v2f vert (appdata v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.uv = v.uv;
				float3 viewVector = mul(unity_CameraInvProjection, float4(v.uv.xy * 2 - 1, 0, -1));
				o.viewVector = mul(unity_CameraToWorld, float4(viewVector, 0));
				return o;
			}

			sampler2D _MainTex;

			sampler2D SkyGradient;
			samplerCUBE SkyCubemap;

			// The planet orbits, so its centre is not the origin. Supplied per frame.
			float3 planetCentre;
			// saturate(dot(dirToSun, up) * 0.5 + 0.5) at the camera, computed on the CPU once
			// per frame rather than per pixel - it does not vary across the screen.
			float sunElevation01;
			float skyIntensity;

			// Forward-scatter glow. The one sun-relative term a (viewElev, sunElev) lookup
			// cannot express, and the most visually obvious thing missing without it.
			float3 glowColour;
			float glowPower;
			float glowStrength;

			float3 sunColour;
			float sunDiscSize;
			float sunDiscBlurA;
			float sunDiscBlurB;

			// How aggressively the sky washes out stars and the moon as it brightens.
			float starFadeStrength;

			// Same disc as the physically based sky - pure ALU, no LUT. Deliberately NOT
			// multiplied by sun transmittance here: that would sample the PBR
			// TransmittanceLUT and smuggle physically based data into the baseline.
			// Thanks to https://www.shadertoy.com/view/slSXRW
			float3 sunDiscWithBloom(float3 rayDir, float3 sunDir)
			{
				static const float PI = 3.1415;
				const float sunSolidAngle = sunDiscSize * PI / 180.0;
				const float minSunCosTheta = cos(sunSolidAngle);

				float cosTheta = dot(rayDir, sunDir);
				if (cosTheta >= minSunCosTheta) return 1;

				float offset = minSunCosTheta - cosTheta;
				float gaussianBloom = exp(-offset * 1000 * sunDiscBlurA) * 0.5;
				float invBloom = 1.0 / (0.02 + offset * 100 * sunDiscBlurB) * 0.01;
				return gaussianBloom + invBloom;
			}

			float4 frag (v2f i) : SV_Target
			{
				float3 viewDir = normalize(i.viewVector);
				float3 dirToSun = _WorldSpaceLightPos0;
				float3 up = normalize(_WorldSpaceCameraPos - planetCentre);

#if defined(SKY_CUBEMAP)
				float3 skyLum = texCUBE(SkyCubemap, viewDir).rgb * skyIntensity;
#else
				// Full -1..1 range rather than horizon-to-zenith only: this is a globe scene,
				// so the camera can be high enough to look down past the horizon.
				float viewElevation01 = saturate(dot(viewDir, up) * 0.5 + 0.5);
				float3 skyLum = tex2D(SkyGradient, float2(viewElevation01, sunElevation01)).rgb * skyIntensity;
#endif

				float cosTheta = saturate(dot(viewDir, dirToSun));
				skyLum += glowColour * pow(cosTheta, glowPower) * glowStrength;
				skyLum += sunDiscWithBloom(viewDir, dirToSun) * sunColour;

				skyLum = toneMap(skyLum);

				// Fade out whatever the stars and moon wrote as the sky brightens. Simplified
				// from DrawSky's version, which additionally tints by sun transmittance - a
				// PBR-only quantity. The alpha channel carries star brightness (Star.shader
				// blends One One) and the moon writes alpha 3.
				float4 originalCol = tex2D(_MainTex, i.uv);
				float backgroundBrightness = originalCol.a;
				float luminance = saturate(dot(skyLum, float3(0.3, 0.5, 0.2)));
				float t = saturate(luminance * starFadeStrength - backgroundBrightness);
				float3 transmittedCol = lerp(originalCol.rgb, 0, t);

				skyLum = transmittedCol + skyLum;

				// Same dither as the physically based path - the sky is a smooth gradient and
				// bands badly without it, especially through the LDR temporary.
				skyLum = blueNoiseDither(skyLum, i.uv, ditherStrength);

				return float4(skyLum, 1);
			}
			ENDCG
		}
	}
}
