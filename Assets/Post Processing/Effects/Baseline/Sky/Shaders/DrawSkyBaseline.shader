// The cheap, non-physically-based baseline sky. Structurally a copy of Hidden/DrawSky - same
// pass shape, star/moon composite, tone map and dither - so only the radiance computation
// (a texture lookup vs. a raymarch) differs between baseline and PBR.
//
// A 2D LUT by (view elevation, sun elevation). A cubemap variant was explored and dropped - it
// could not respond to the sun moving - so this used to be one of two keyword-selected paths;
// see git history if that comparison is ever wanted again.
//
// The gradient LUT stores linear radiance, not tone-mapped colour, so it shares toneMap() with
// the physically based sky.
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

			#include "UnityCG.cginc"
			#include "Assets/Scripts/Shader Common/DrawAtmosphereCommon.hlsl"
			#include "Assets/Scripts/Shader Common/MathInternal.hlsl"

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

			// The planet orbits, so its centre is not the origin. Supplied per frame.
			float3 planetCentre;
			// saturate(dot(dirToSun, up) * 0.5 + 0.5), computed once per frame on the CPU
			// since it does not vary across the screen.
			float sunElevation01;
			float skyIntensity;

			// Forward-scatter glow: the one sun-relative term a (viewElev, sunElev) lookup can't express.
			float3 glowColour;
			float glowPower;
			float glowStrength;

			float3 sunColour;
			float sunDiscSize;
			float sunDiscBlurA;
			float sunDiscBlurB;

			// How aggressively the sky washes out stars and the moon as it brightens.
			float starFadeStrength;

			// Same disc as the physically based sky, pure ALU. Not multiplied by sun
			// transmittance, which would smuggle PBR data into the baseline.
			// Thanks to https://www.shadertoy.com/view/slSXRW
			float3 sunDiscWithBloom(float3 rayDir, float3 sunDir)
			{
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

				// Full -1..1 range, not just horizon-to-zenith: on a globe the camera can look
				// down past the horizon.
				float viewElevation01 = saturate(dot(viewDir, up) * 0.5 + 0.5);
				float3 skyLum = tex2D(SkyGradient, float2(viewElevation01, sunElevation01)).rgb * skyIntensity;

				float cosTheta = saturate(dot(viewDir, dirToSun));
				skyLum += glowColour * pow(cosTheta, glowPower) * glowStrength;
				skyLum += sunDiscWithBloom(viewDir, dirToSun) * sunColour;

				skyLum = toneMap(skyLum);

				// Fades out whatever the stars/moon wrote as the sky brightens. Simplified from
				// DrawSky's version, which also tints by sun transmittance (a PBR-only quantity).
				// Alpha carries star brightness (Star.shader blends One One); the moon writes alpha 3.
				float4 originalCol = tex2D(_MainTex, i.uv);
				float backgroundBrightness = originalCol.a;
				float luminance = saturate(dot(skyLum, float3(0.3, 0.5, 0.2)));
				float t = saturate(luminance * starFadeStrength - backgroundBrightness);
				float3 transmittedCol = lerp(originalCol.rgb, 0, t);

				skyLum = transmittedCol + skyLum;

				// Same dither as the physically based path; the smooth gradient bands without it.
				skyLum = blueNoiseDither(skyLum, i.uv, ditherStrength);

				return float4(skyLum, 1);
			}
			ENDCG
		}
	}
}
