// Guarded because SurfaceLighting.hlsl now pulls this in as well, so a shader that includes
// both it and this header directly would otherwise redeclare every uniform below.
#ifndef DRAW_ATMOSPHERE_COMMON_INCLUDED
#define DRAW_ATMOSPHERE_COMMON_INCLUDED

// The tone map itself lives in ToneMapCommon.hlsl, because a compute shader needs it and cannot
// include this file - getBlueNoise below reads _ScreenParams and samples with implicit derivatives,
// neither of which exists outside a fragment stage. Consumers of this header are unaffected: they
// get toneMap transitively, exactly as before.
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
	// max(0, ...): r2 - 0.25 is negative on the whole of the f1 branch, and HLSL evaluates both
	// operands of ?: before selecting. The NaN is discarded so nothing is visibly wrong, but a
	// NaN that only survives because of which side of a select it lands on is not worth keeping.
	float f2 = 1.0 - sqrt(max(0, r2 - 0.25));
	return (v < 0.5) ? f1 : f2;
}

/// The blue noise texture at one screen pixel per texel, unmodified - values in [0, 1).
///
/// getBlueNoise below is this remapped to a triangular distribution and rescaled, which is what
/// DITHERING wants: signed, centred, and heavier in the middle so the error it hides averages out.
/// A raymarch start offset wants none of that. It wants a uniform fraction of one step, because
/// the thing being randomised is *where in the interval* the sample sits, and every position in
/// that interval is equally valid. Feeding it the dither distribution instead lands the offset in
/// [-0.5, 1.5] with a mean of 0.5 - see the note at the call site in Clouds.shader.
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
