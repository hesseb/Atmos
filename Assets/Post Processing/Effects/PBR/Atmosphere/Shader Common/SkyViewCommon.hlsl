#ifndef SKY_VIEW_COMMON_INCLUDED
#define SKY_VIEW_COMMON_INCLUDED

// Assets-absolute path so this header can be included from outside the atmosphere tree
#include "Assets/Post Processing/Effects/PBR/Atmosphere/Shader Common/TransmittanceCommon.hlsl"
#include "Assets/Scripts/Shader Common/MathInternal.hlsl"

float2 skyViewLutSize;
float skyViewLutRadius;
float skyViewHorizonV;
sampler2D SkyViewLUT;

float skyViewHorizonZenith(float radius) {
	return PI - asin(saturate(planetRadius / max(radius, planetRadius)));
}

/// Direction to LUT coordinates.
float2 skyViewLutUv(float radius, float cosViewZenith, float cosAzimuth) {
	float thetaH = skyViewHorizonZenith(radius);
	float theta = acos(clamp(cosViewZenith, -1, 1));

	float v;
	if (theta < thetaH) {
		v = skyViewHorizonV * (1 - sqrt(saturate(1 - theta / max(1e-4, thetaH))));
	}
	else {
		float beta = max(1e-4, PI - thetaH);
		v = skyViewHorizonV + (1 - skyViewHorizonV) * sqrt(saturate((theta - thetaH) / beta));
	}

	float u = sqrt(saturate(0.5 - 0.5 * clamp(cosAzimuth, -1, 1)));
	return lutUnitToSubUv(float2(u, v), skyViewLutSize);
}

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
		float beta = PI - thetaH;
		float c = (unit.y - skyViewHorizonV) / max(1e-4, 1 - skyViewHorizonV);
		theta = thetaH + beta * c * c;
	}

	cosViewZenith = cos(theta);
	cosAzimuth = 1 - 2 * unit.x * unit.x;   // inverse of sqrt(0.5 - 0.5c)
}

float3 sampleSkyView(float3 up, float3 dir, float3 sunDir) {
	float3 sunTangent = sunDir - up * dot(sunDir, up);
	float3 dirTangent = dir - up * dot(dir, up);
	
	float sunLen = length(sunTangent);
	float dirLen = length(dirTangent);
	
	float cosAzimuth = (sunLen > 1e-4 && dirLen > 1e-4) ? dot(sunTangent, dirTangent) / (sunLen * dirLen) : 1;
	float cosViewZenith = dot(up, dir);

	float2 uv = skyViewLutUv(skyViewLutRadius, cosViewZenith, cosAzimuth);
	return tex2Dlod(SkyViewLUT, float4(uv, 0, 0)).rgb;
}

#endif
