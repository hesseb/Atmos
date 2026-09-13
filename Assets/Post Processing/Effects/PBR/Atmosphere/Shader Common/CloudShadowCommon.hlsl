#ifndef CLOUD_SHADOW_COMMON_INCLUDED
#define CLOUD_SHADOW_COMMON_INCLUDED

#include "Assets/Scripts/Shader Common/GeoMath.hlsl"

sampler2D CloudShadowMap;
float cloudShadowStrength;
float cloudShaftStrength;
float cloudDeckRadius;

/// Where a ray from `pos` along `dir` meets the cloud deck's sphere
// Helper for other functions here
float cloudDeckRayHit(float3 pos, float3 dir) {
	float b = dot(pos, dir);
	float c = dot(pos, pos) - cloudDeckRadius * cloudDeckRadius;
	float d = b * b - c;
	
	if (d < 0)
	{
		return -1;
	}

	float s = sqrt(d);
	return (c < 0) ? (-b + s) : (-b - s);
}

// How much sun reaches this point on the ground?
float cloudShadow(float3 sphereNormal) {
	if (cloudShadowStrength <= 0)
	{
		return 1;
	}

	float shadow = tex2Dlod(CloudShadowMap, float4(pointToUV(sphereNormal), 0, 0)).r;
	return lerp(1, shadow, cloudShadowStrength);
}

// How much sun reaches this point in the air?
float cloudSunVisibility(float3 pos, float3 dirToSun) {
	if (cloudShaftStrength <= 0 || cloudDeckRadius <= 0)
	{
		return 1;
	}

	float t = cloudDeckRayHit(pos, dirToSun);
	if (t < 0) // the sun ray never meets the deck, or the deck is behind this point
	{
		return 1;
	}   

	float3 crossing = normalize(pos + dirToSun * t);
	float shadow = tex2Dlod(CloudShadowMap, float4(pointToUV(crossing), 0, 0)).r;
	return lerp(1, shadow, cloudShaftStrength);
}

#endif
