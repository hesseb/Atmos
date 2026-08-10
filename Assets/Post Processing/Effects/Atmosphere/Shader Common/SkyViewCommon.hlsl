// The sky-view LUT's parameterisation: sky radiance as a function of *direction*, not of screen
// position.
//
// The existing `sky` texture cannot answer this. It is indexed by `calculateViewDir(uv)` over the
// current camera's four frustum corners, so it only holds directions that happen to be on screen -
// which a reflected ray routinely is not. This LUT is what lets the ocean ask "what colour is the
// sky in the direction I am reflecting towards".
//
// Hillaire 2020 section 5.3. Two deliberate differences from his, both explained below:
// `skyViewHorizonV`, and the radius being a uniform rather than the camera's.
//
// Everything that writes or reads this LUT includes this file, and nothing restates the mapping -
// the same rule as TransmittanceCommon.hlsl, for the same reason: a write/read mismatch produces a
// plausible sky rather than an obvious fault.

#ifndef SKY_VIEW_COMMON_INCLUDED
#define SKY_VIEW_COMMON_INCLUDED

// For planetRadius, and lutUnitToSubUv / lutSubUvToUnit. Assets-absolute so this header can be
// included from outside the atmosphere tree - Ocean.shader does exactly that.
#include "Assets/Post Processing/Effects/Atmosphere/Shader Common/TransmittanceCommon.hlsl"

// Own constant rather than AtmosphereCommon's PI: the ocean includes this header without including
// AtmosphereCommon (which declares the global `dirToSun` and would shadow the ocean's own), and a
// distinct name cannot collide when a compute includes both.
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

/// Direction to LUT coordinates.
///
/// v crowds hard toward the horizon (`1 - sqrt(1 - theta/thetaH)`, whose derivative diverges
/// there), because the horizon is where sunset colour lives and where a grazing ocean reflection
/// lands. A linear mapping would give it no more resolution than the zenith, where the sky is
/// nearly constant.
///
/// u is indexed by the cosine of the azimuth *relative to the sun*, which does two jobs at once:
/// it folds the two sun-symmetric halves of the sky onto each other - lossless, because a
/// spherically symmetric atmosphere with one sun is exactly mirror-symmetric about the sun-zenith
/// plane - and it crowds texels toward the sun, where the Mie forward lobe is. It also means the
/// LUT does not need rebuilding when the camera yaws.
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

/// Sky radiance arriving from `dir`, for a surface whose local up is `up`.
///
/// `sunDir` is a parameter rather than a global on purpose: AtmosphereCommon.hlsl owns the only
/// `dirToSun` uniform, and a header that redeclared it would shadow the local of any forward
/// shader that includes this one.
///
/// Radiance, pre-tone-map, exactly like the `sky` texture - so a consumer writing into the camera
/// colour buffer must tone map it first, with the same constants the sky pass used.
float3 sampleSkyView(float3 up, float3 dir, float3 sunDir) {
	float cosViewZenith = dot(up, dir);

	// Azimuth of `dir` relative to the sun, both projected onto the local horizontal plane.
	// Degenerate when either is near vertical - and in exactly those cases the sky is nearly
	// azimuthally symmetric, so the fallback is unobservable.
	float3 sunTangent = sunDir - up * dot(sunDir, up);
	float3 dirTangent = dir - up * dot(dir, up);
	float sunLen = length(sunTangent);
	float dirLen = length(dirTangent);
	float cosAzimuth = (sunLen > 1e-4 && dirLen > 1e-4) ? dot(sunTangent, dirTangent) / (sunLen * dirLen) : 1;

	float2 uv = skyViewLutUv(skyViewLutRadius, cosViewZenith, cosAzimuth);
	return tex2Dlod(SkyViewLUT, float4(uv, 0, 0)).rgb;
}

#endif
