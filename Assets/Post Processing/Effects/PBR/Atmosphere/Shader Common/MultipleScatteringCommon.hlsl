#ifndef MULTIPLE_SCATTERING_COMMON_INCLUDED
#define MULTIPLE_SCATTERING_COMMON_INCLUDED

#include "Assets/Post Processing/Effects/PBR/Atmosphere/Shader Common/TransmittanceCommon.hlsl"

float2 multipleScatteringLutSize;
float multipleScatteringStrength;
float groundAlbedo;
sampler2D MultipleScatteringLUT;

float2 multipleScatteringLutUv(float radius, float cosSunZenith) {
	float2 unit = float2(saturate(cosSunZenith * 0.5 + 0.5),
	                     saturate((radius - planetRadius) / atmosphereThickness));
	return lutUnitToSubUv(unit, multipleScatteringLutSize);
}

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
