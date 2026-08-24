// The multiple-scattering LUT's parameterisation, defined once so writer and readers can't
// disagree. Requires TransmittanceCommon.hlsl for planetRadius / atmosphereThickness.

#ifndef MULTIPLE_SCATTERING_COMMON_INCLUDED
#define MULTIPLE_SCATTERING_COMMON_INCLUDED

float2 multipleScatteringLutSize;

// 0 disables the term entirely - the single-scattering-only control condition the comparison
// renderer profiles need.
float multipleScatteringStrength;

// Ground reflectance for the LUT's Lambertian bounce; omitting it noticeably darkens the lower
// atmosphere at high sun angles.
float groundAlbedo;

// Global rather than threaded through raymarch's signature, unlike the transmittance LUT (which
// needs to be a parameter since the writing compute has a RWTexture2D of the same name in scope).
sampler2D MultipleScatteringLUT;

// Half-texel inset comes from LutMapping.hlsl, via TransmittanceCommon.hlsl.

/// Radius and sun-zenith cosine to LUT coordinates. Depends only on altitude and sun elevation -
/// not view direction (orders beyond the first are isotropic) or sun azimuth (spherical symmetry) -
/// which is what makes a 32x32 texture sufficient.
float2 multipleScatteringLutUv(float radius, float cosSunZenith) {
	float2 unit = float2(saturate(cosSunZenith * 0.5 + 0.5),
	                     saturate((radius - planetRadius) / atmosphereThickness));
	return lutUnitToSubUv(unit, multipleScatteringLutSize);
}

/// The inverse, used by the compute that fills the LUT.
void multipleScatteringLutParams(float2 uv, out float radius, out float cosSunZenith) {
	float2 unit = lutSubUvToUnit(uv, multipleScatteringLutSize);
	cosSunZenith = unit.x * 2.0 - 1.0;
	radius = planetRadius + unit.y * atmosphereThickness;
}

float3 sampleMultipleScattering(float3 pos, float3 sunDir) {
	float radius = length(pos);
	float cosSunZenith = dot(pos / radius, sunDir);
	return tex2Dlod(MultipleScatteringLUT, float4(multipleScatteringLutUv(radius, cosSunZenith), 0, 0)).rgb;
}

#endif
