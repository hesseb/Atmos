// The sky-view LUT's parameterisation: sky radiance as a function of direction rather than screen
// position, which is what lets the ocean ask "what colour is the sky where I'm reflecting towards" -
// the `sky` texture only holds directions on screen. Hillaire 2020 §5.3, with skyViewHorizonV and a
// uniform (not camera) radius as the two deliberate deviations.

#ifndef SKY_VIEW_COMMON_INCLUDED
#define SKY_VIEW_COMMON_INCLUDED

// Assets-absolute so this header can be included from outside the atmosphere tree - Ocean.shader
// does exactly that, for planetRadius and the LUT mapping helpers.
#include "Assets/Post Processing/Effects/Atmosphere/Shader Common/TransmittanceCommon.hlsl"

// Own PI rather than AtmosphereCommon's, since including AtmosphereCommon here would shadow the
// ocean's own dirToSun with the global one.
static const float SKYVIEW_PI = 3.14159265359;

float2 skyViewLutSize;

/// Radius the LUT was baked at. Sea level for the ocean's reflection - see the note in
/// AtmosphereEffect.skyViewLutAltitude for why that, and not the camera's altitude.
float skyViewLutRadius;

/// Where the horizon sits in v. 1 stores the upper hemisphere only, which is all a sea-level LUT
/// can hold; 0.5 is Hillaire's full-sphere split, for a future consumer at altitude.
float skyViewHorizonV;

sampler2D SkyViewLUT;

/// Zenith angle of the horizon seen from `radius`: exactly pi/2 at the surface, and larger from
/// altitude, where the horizon dips below the horizontal.
float skyViewHorizonZenith(float radius) {
	return SKYVIEW_PI - asin(saturate(planetRadius / max(radius, planetRadius)));
}

/// Direction to LUT coordinates. V crowds hard toward the horizon, where sunset colour and grazing
/// reflections live. U indexes the cosine of azimuth relative to the sun, which folds the sky's two
/// mirror-symmetric halves together, crowds texels toward the Mie forward lobe, and means the LUT
/// never needs rebuilding when the camera yaws.
float2 skyViewLutUv(float radius, float cosViewZenith, float cosAzimuth) {
	float thetaH = skyViewHorizonZenith(radius);
	float theta = acos(clamp(cosViewZenith, -1, 1));

	float v;
	if (theta < thetaH) {
		v = skyViewHorizonV * (1 - sqrt(saturate(1 - theta / max(1e-4, thetaH))));
	}
	else {
		float beta = max(1e-4, SKYVIEW_PI - thetaH);
		v = skyViewHorizonV + (1 - skyViewHorizonV) * sqrt(saturate((theta - thetaH) / beta));
	}

	float u = sqrt(saturate(0.5 - 0.5 * clamp(cosAzimuth, -1, 1)));
	return lutUnitToSubUv(float2(u, v), skyViewLutSize);
}

/// The inverse, used by the compute that fills the LUT, so the two can never disagree.
void skyViewLutParams(float2 uv, float radius, out float cosViewZenith, out float cosAzimuth) {
	float2 unit = lutSubUvToUnit(uv, skyViewLutSize);
	float thetaH = skyViewHorizonZenith(radius);

	float theta;
	if (unit.y < skyViewHorizonV) {
		// Inverse of 1 - sqrt(1 - x).
		float c = unit.y / max(1e-4, skyViewHorizonV);
		theta = thetaH * (1 - (1 - c) * (1 - c));
	}
	else {
		float beta = SKYVIEW_PI - thetaH;
		float c = (unit.y - skyViewHorizonV) / max(1e-4, 1 - skyViewHorizonV);
		theta = thetaH + beta * c * c;
	}

	cosViewZenith = cos(theta);
	cosAzimuth = 1 - 2 * unit.x * unit.x;   // inverse of sqrt(0.5 - 0.5c)
}

/// Sky radiance from `dir`, for a surface with local up `up`. `sunDir` is a parameter rather than a
/// global since AtmosphereCommon.hlsl owns the only `dirToSun` uniform. Pre-tone-map, like the `sky`
/// texture - callers writing to the camera buffer must tone map with the same constants the sky
/// pass used.
float3 sampleSkyView(float3 up, float3 dir, float3 sunDir) {
	float cosViewZenith = dot(up, dir);

	// Azimuth of `dir` relative to the sun, projected onto the local horizontal; degenerates near
	// vertical, where the sky is nearly azimuthally symmetric anyway, so the fallback is unobservable.
	float3 sunTangent = sunDir - up * dot(sunDir, up);
	float3 dirTangent = dir - up * dot(dir, up);
	float sunLen = length(sunTangent);
	float dirLen = length(dirTangent);
	float cosAzimuth = (sunLen > 1e-4 && dirLen > 1e-4) ? dot(sunTangent, dirTangent) / (sunLen * dirLen) : 1;

	float2 uv = skyViewLutUv(skyViewLutRadius, cosViewZenith, cosAzimuth);
	return tex2Dlod(SkyViewLUT, float4(uv, 0, 0)).rgb;
}

#endif
