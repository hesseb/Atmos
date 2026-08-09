// The transmittance LUT's parameterisation, and the planet dimensions it depends on.
//
// Split out because the mapping had two byte-identical copies - one here, one inlined in
// DrawSky.shader, which does not include AtmosphereCommon.hlsl. The mapping is about to be
// replaced with Bruneton's distance parameterisation, and a second copy would drift silently:
// the LUT would be written with one mapping and read with another, which produces a plausible
// sky rather than an obvious failure.
//
// Everything that writes or reads the transmittance LUT includes this file, and nothing
// restates the mapping.

#ifndef TRANSMITTANCE_COMMON_INCLUDED
#define TRANSMITTANCE_COMMON_INCLUDED

// Planet dimensions, in world units. Declared here rather than by the includers so there is
// exactly one declaration of each.
float atmosphereThickness;
float atmosphereRadius;
float planetRadius;

/// Maps a position and a direction to transmittance-LUT coordinates.
///
/// U is linear in cos(zenith angle of the direction), V linear in altitude. This is NOT
/// Hillaire's mapping - his follows Bruneton, whose distance parameterisation both
/// concentrates texels near the horizon and stores only rays that miss the ground.
float2 transmittanceLutUv(float3 pos, float3 dir) {
	float dstFromCentre = length(pos);
	float height01 = saturate((dstFromCentre - planetRadius) / atmosphereThickness);

	float u = 1 - (dot(pos / dstFromCentre, dir) * 0.5 + 0.5);
	return float2(u, height01);
}

/// The inverse of transmittanceLutUv: LUT coordinates back to a radius and a cosine.
/// Used by the compute that fills the LUT, so the two can never disagree.
void transmittanceLutParams(float2 uv, out float radius, out float cosZenith) {
	radius = planetRadius + atmosphereThickness * uv.y;
	cosZenith = 1 - 2 * uv.x;
}

float3 sampleTransmittanceLUT(sampler2D lut, float3 pos, float3 dir) {
	return tex2Dlod(lut, float4(transmittanceLutUv(pos, dir), 0, 0)).rgb;
}

#endif
