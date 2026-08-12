// The tone map, on its own so a COMPUTE shader can reach it.
//
// Split out of DrawAtmosphereCommon.hlsl, which cannot be included from a compute: its
// getBlueNoise reads _ScreenParams and samples with tex2D, whose implicit derivatives do not exist
// outside a fragment stage. Nothing here has that problem - it is uniforms and arithmetic.
//
// That mattered as soon as something other than a fragment shader needed to produce a value at the
// same exposure as the sky. The cloud deck's downward radiance is baked by a compute and then read
// by the ocean and the terrain, and it has to sit at the exposure they already work in, or the sea
// reflects a deck that does not match the one drawn above it.
//
// DrawAtmosphereCommon.hlsl includes this and keeps the dither, so its own consumers are unchanged.

#ifndef TONE_MAP_COMMON_INCLUDED
#define TONE_MAP_COMMON_INCLUDED

float intensity;
float contrast;
float whitePoint;

// https://64.github.io/tonemapping/
float3 reinhard_extended(float3 v, float max_white)
{
	float3 numerator = v * (1.0f + (v / (max_white * max_white)));
	return numerator / (1.0f + v);
}

/// float3 h, not float.
///
/// It was `float h`, which silently truncated a float3 expression to its RED channel and then
/// applied red's blend factor to green and blue as well. Inherited unchanged when this moved out of
/// DrawAtmosphereCommon.hlsl; the compiler had been warning about it all along and only said so
/// audibly once a compute shader started compiling the file.
///
/// It matters where the channels straddle the blend window differently. This is called as
/// smoothMax(lum, -0.05, 0.05), a soft floor just below zero, and the tone map's contrast pivot
/// pushes dark pixels negative before it - lerp(0.5, 0, 1.45) is -0.225 - so the floor is doing real
/// work at night, which is exactly when the channels differ most in sign. A pixel with red just
/// above zero and blue at -0.08 used to leave blue unfloored.
///
/// Small, and mostly masked downstream by the max(0, toneMap(...)) that SurfaceLighting applies -
/// but the sky pass writes toneMap's output to the colour buffer with no such clamp.
float3 smoothMax (float3 a, float3 b, float k) {
	k = -abs (k);
	float3 h = saturate((b - a + k) / (2 * k));
	return a * h + b * (1 - h) - k * h * (1 - h);
}

float3 toneMap(float3 lum) {
	lum *= intensity;
	lum = lerp(0.5, lum, contrast);
	lum = reinhard_extended(lum, whitePoint);

	lum = smoothMax(lum, -0.05, 0.05);

	return lum;
}

float3 toneMapA(float3 lum) {
	lum *= intensity;
	lum = lerp(0.5, lum, contrast);
	return lum;
}

float3 toneMapB(float3 lum) {
	lum = reinhard_extended(lum, whitePoint);
	lum = smoothMax(lum, -0.05, 0.05);
	return lum;
}

#endif
