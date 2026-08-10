// The transmittance LUT's parameterisation, and the planet dimensions it depends on.
//
// Split out because the mapping had two byte-identical copies - one here, one inlined in
// DrawSky.shader, which does not include AtmosphereCommon.hlsl. A second copy would drift
// silently: the LUT would be written with one mapping and read with another, which produces a
// plausible sky rather than an obvious failure.
//
// Everything that writes or reads the transmittance LUT includes this file, and nothing
// restates the mapping.

#ifndef TRANSMITTANCE_COMMON_INCLUDED
#define TRANSMITTANCE_COMMON_INCLUDED

#include "LutMapping.hlsl"

// Planet dimensions, in world units. Declared here rather than by the includers so there is
// exactly one declaration of each.
float atmosphereThickness;
float atmosphereRadius;
float planetRadius;
float2 transmittanceLutSize;

/// Whether a ray leaving `radius` at zenith cosine `cosZenith` meets the ground.
///
/// Needed as a plain algebraic test rather than a ray-sphere call, because DrawSky.shader
/// includes this header without AtmosphereCommon.hlsl and so has no rayIntersectSphere.
/// A ray descends into the planet exactly when it points below the horizon, whose cosine is
/// -sqrt(1 - (Rg/r)^2).
bool transmittanceRayHitsGround(float radius, float cosZenith) {
	return cosZenith < 0
		&& radius * radius * (cosZenith * cosZenith - 1) + planetRadius * planetRadius >= 0;
}

/// Bruneton's distance parameterisation.
///
/// U is the distance to the top of the atmosphere, remapped between its minimum (straight up)
/// and its maximum (grazing the horizon); V is the distance to the horizon, over its value at
/// the top of the atmosphere. This replaces a mapping linear in cos(zenith) and linear in
/// altitude, and it buys two distinct things:
///
/// 1. Only rays that MISS the ground are representable. The old mapping spent half its width at
///    ground level on rays marching down through the planet, which the caller then discarded -
///    so this is close to a 2x resolution gain before any redistribution.
/// 2. Texels concentrate near the horizon, where transmittance varies fastest.
///
/// Temper the second: Bruneton designed the crowding for Earth's Rt/Rg = 1.0157, and here it is
/// 1.736, so the redistribution buys much less than it does for Earth. The first gain and the
/// traceability to a published parameterisation are the real reasons this is worth having.
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

	// Occlusion lives here, and it has to, because the LUT no longer contains it.
	//
	// Bruneton's mapping stores only rays that miss the ground, so a below-horizon direction
	// clamps to the horizon texel - which at ground level still transmits about a quarter of its
	// blue. Under the old mapping such a ray was stored explicitly and marched through the
	// planet, coming back near zero, so callers could get away with not testing.
	//
	// The aerial perspective is exactly such a caller: it passes earthShadowRadius = 0 and has
	// no test of its own, so without this the night side would be lit by a sun below the
	// horizon. Putting the test in the shared sampler means no caller can forget it.
	if (transmittanceRayHitsGround(radius, cosZenith)) { return 0; }

	return tex2Dlod(lut, float4(transmittanceLutUv(radius, cosZenith), 0, 0)).rgb;
}

#endif
