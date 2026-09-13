#ifndef DRAW_ATMOSPHERE_COMMON_INCLUDED
#define DRAW_ATMOSPHERE_COMMON_INCLUDED

#include "Assets/Scripts/Shader Common/ToneMapCommon.hlsl"

// Dithering
float ditherStrength;
sampler2D BlueNoise;
float4 BlueNoise_TexelSize;

// Remap noise to triangular distribution
// See pg. 45 to 57 of www.gdcvault.com/play/1023002/Low-Complexity-High-Fidelity-INSIDE
// Thanks to https://www.shadertoy.com/view/MslGR8 See also https://www.shadertoy.com/view/4t2SDh
float remap_noise_tri_erp(float v)
{
	float r2 = 0.5 * v;
	float f1 = sqrt(r2);
	float f2 = 1.0 - sqrt(max(0, r2 - 0.25));

	return (v < 0.5) ? f1 : f2;
}

float3 getBlueNoiseRaw(float2 uv) {
	float2 screenSize = _ScreenParams.xy;
	uv = (uv * screenSize) * BlueNoise_TexelSize.xy;
	return tex2D(BlueNoise, uv).rgb;
}

float3 getBlueNoise(float2 uv) {
	float3 blueNoise = getBlueNoiseRaw(uv);
	float3 m = 0;
	m.r = remap_noise_tri_erp(blueNoise.r);
	m.g = remap_noise_tri_erp(blueNoise.g);
	m.b = remap_noise_tri_erp(blueNoise.b);

	float3 weightedNoise = (m * 2.0 - 0.5);
	return weightedNoise;
}

float3 blueNoiseDither(float3 col, float2 uv, float strength) {
	float3 weightedNoise = getBlueNoise(uv) / 255.0 * strength;

	return col + weightedNoise;
}

#endif
