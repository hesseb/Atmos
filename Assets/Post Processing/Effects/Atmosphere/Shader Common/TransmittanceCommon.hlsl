// The transmittance LUT's parameterisation and the planet dimensions it depends on, shared so
// every writer and reader agrees - a second copy would drift silently into a plausible-looking sky.

#ifndef TRANSMITTANCE_COMMON_INCLUDED
#define TRANSMITTANCE_COMMON_INCLUDED

// Assets-absolute since this header is also included from outside the atmosphere tree (Ocean.shader,
// for its sun glint), and a relative include only resolves from within this folder.
#include "Assets/Post Processing/Effects/Atmosphere/Shader Common/LutMapping.hlsl"

// Planet dimensions in world units, declared once here rather than by each includer.
float atmosphereThickness;
float atmosphereRadius;
float planetRadius;
float2 transmittanceLutSize;

/// Whether a ray leaving `radius` at zenith cosine `cosZenith` meets the ground. A plain algebraic
/// test rather than a ray-sphere call, since DrawSky.shader includes this header without
/// AtmosphereCommon.hlsl and so has no rayIntersectSphere.
bool transmittanceRayHitsGround(float radius, float cosZenith) {
	return cosZenith < 0
		&& radius * radius * (cosZenith * cosZenith - 1) + planetRadius * planetRadius >= 0;
}

/// Bruneton's distance parameterisation: U is the distance to the top of the atmosphere, remapped
/// between straight-up and horizon-grazing; V is the horizon distance over its value at the top of
/// the atmosphere. Only rays that miss the ground are representable, and texels crowd near the
/// horizon, where transmittance varies fastest.
float2 transmittanceLutUv(float radius, float cosZenith) {
	radius = clamp(radius, planetRadius, atmosphereRadius);

	// H: ground-level horizon distance. rho: horizon distance from `radius`.
	float H = sqrt(max(0, atmosphereRadius * atmosphereRadius - planetRadius * planetRadius));
	float rho = sqrt(max(0, radius * radius - planetRadius * planetRadius));

	// Distance from (radius, cosZenith) to the top of the atmosphere.
	float discriminant = radius * radius * (cosZenith * cosZenith - 1) + atmosphereRadius * atmosphereRadius;
	float d = max(0, -radius * cosZenith + sqrt(max(0, discriminant)));

	float dMin = atmosphereRadius - radius;  // straight up
	float dMax = rho + H;                    // along the horizon

	float xMu = dMax > dMin ? (d - dMin) / (dMax - dMin) : 0;
	float xR = H > 0 ? rho / H : 0;

	return lutUnitToSubUv(float2(saturate(xMu), saturate(xR)), transmittanceLutSize);
}

/// The inverse of transmittanceLutUv: LUT coordinates back to a radius and a cosine.
/// Used by the compute that fills the LUT, so the two can never disagree.
void transmittanceLutParams(float2 uv, out float radius, out float cosZenith) {
	float2 unit = lutSubUvToUnit(uv, transmittanceLutSize);

	float H = sqrt(max(0, atmosphereRadius * atmosphereRadius - planetRadius * planetRadius));
	float rho = H * unit.y;
	radius = sqrt(rho * rho + planetRadius * planetRadius);

	float dMin = atmosphereRadius - radius;
	float dMax = rho + H;
	float d = dMin + unit.x * (dMax - dMin);

	// Inverting d = -r*mu + sqrt(r^2 mu^2 + Rt^2 - r^2) gives mu = (Rt^2 - r^2 - d^2)/(2rd),
	// and Rt^2 - r^2 is H^2 - rho^2 by definition of both.
	cosZenith = d <= 0 ? 1 : (H * H - rho * rho - d * d) / (2 * radius * d);
	cosZenith = clamp(cosZenith, -1, 1);
}

float3 sampleTransmittanceLUT(sampler2D lut, float3 pos, float3 dir) {
	float radius = length(pos);
	float cosZenith = dot(pos / radius, dir);

	// Occlusion has to live here: Bruneton's mapping only stores rays that miss the ground, so a
	// below-horizon direction would otherwise clamp to the horizon texel instead of returning zero -
	// which callers like the aerial perspective (no earth-shadow test of their own) rely on.
	if (transmittanceRayHitsGround(radius, cosZenith)) { return 0; }

	return tex2Dlod(lut, float4(transmittanceLutUv(radius, cosZenith), 0, 0)).rgb;
}

#endif
