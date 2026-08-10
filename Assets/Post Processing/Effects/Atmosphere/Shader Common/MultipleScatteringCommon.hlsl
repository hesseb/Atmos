// The multiple-scattering LUT's parameterisation, defined once so the compute that writes it
// and the marches that read it cannot disagree. Same reasoning as TransmittanceCommon.hlsl:
// a write/read mismatch in a LUT mapping produces a plausible sky rather than a visible fault,
// which is the worst possible failure mode for something a thesis measures.
//
// Requires TransmittanceCommon.hlsl for planetRadius / atmosphereThickness.

#ifndef MULTIPLE_SCATTERING_COMMON_INCLUDED
#define MULTIPLE_SCATTERING_COMMON_INCLUDED

float2 multipleScatteringLutSize;

// 0 disables the term entirely, which is what the comparison renderer profiles need: a
// single-scattering-only image is the thing multiple scattering has to be measured against.
float multipleScatteringStrength;

// Reflectance of the ground for the Lambertian bounce inside the LUT. Light that leaves the
// surface and scatters back down is a real part of the multiply-scattered field, and omitting
// it darkens the lower atmosphere noticeably at high sun angles.
float groundAlbedo;

// Declared as a global rather than threaded through raymarch's signature, unlike the
// transmittance LUT. That one is a parameter because the compute that *writes* it needs a
// RWTexture2D of the same name in scope; nothing here has that conflict, and a global avoids
// touching all four raymarch call sites.
sampler2D MultipleScatteringLUT;

// Half-texel inset comes from LutMapping.hlsl, via TransmittanceCommon.hlsl.

/// Maps a radius and a sun-zenith cosine to multiple-scattering LUT coordinates.
///
/// The LUT depends only on altitude and sun elevation - not on the view direction, because
/// orders beyond the first are treated as isotropic, and not on the sun's azimuth, because the
/// atmosphere is spherically symmetric. That is what makes a 32x32 texture sufficient.
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
