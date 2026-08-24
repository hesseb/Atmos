// Cloud-deck sun occlusion. cloudShadow answers for ground points (projected radially);
// cloudSunVisibility answers for air points along the actual sun ray, which is what produces light
// shafts. Own header since AtmosphereCommon.hlsl and SurfaceLighting.hlsl can't include each other.

#ifndef CLOUD_SHADOW_COMMON_INCLUDED
#define CLOUD_SHADOW_COMMON_INCLUDED

#include "Assets/Scripts/Shader Common/GeoMath.hlsl"

/// Written by whichever cloud renderer is active, equirectangular and indexed by pointToUV like
/// everything else on this globe - view-independent, so it costs one tap and no new plumbing.
sampler2D CloudShadowMap;

/// Surfaces. 0 when the clouds are off.
float cloudShadowStrength;

/// The air, priced separately from cloudShadowStrength since the two have very different costs
/// (a tap per pixel vs. a tap per raymarch step). 0 is identity.
float cloudShaftStrength;

/// Radius the cloud deck is treated as sitting at. 0 means no deck has been published yet.
float cloudDeckRadius;

/// Sunlight reaching the ground through the cloud deck, as a multiplier. Sun-only - not folded into
/// sampleLightColour, which is also called with the moon's direction - so callers apply it to their
/// sun term specifically. tex2Dlod rather than tex2D since this header is also included by computes.
float cloudShadow(float3 sphereNormal) {
	if (cloudShadowStrength <= 0) { return 1; }
	float shadow = tex2Dlod(CloudShadowMap, float4(pointToUV(sphereNormal), 0, 0)).r;
	return lerp(1, shadow, cloudShadowStrength);
}

/// Where a ray from `pos` along `dir` meets the cloud deck's sphere, or a negative sentinel on a
/// miss. `c`'s sign distinguishes entering the deck from exiting it, so one formula covers both a
/// ray starting outside the deck (light shafts, below) and one starting inside it (a surface
/// reflecting the deck above it, in SurfaceLighting.hlsl's sampleCloudSkyAlong - the only other
/// place this sphere is tested), with no separate altitude test either way.
float cloudDeckRayHit(float3 pos, float3 dir) {
	float b = dot(pos, dir);
	float c = dot(pos, pos) - cloudDeckRadius * cloudDeckRadius;
	float d = b * b - c;
	if (d < 0) { return -1; }

	float s = sqrt(d);
	return (c < 0) ? (-b + s) : (-b - s);
}

/// Sunlight reaching a point IN THE AIR, sampled where its actual sun ray crosses the deck rather
/// than by projecting radially like cloudShadow - which is what gives light shafts their lean. No
/// altitude cutoff: a ray above the deck can still cross it at a shallow angle when the sun is low,
/// so points genuinely clear of the deck fall out of the intersection test on their own.
float cloudSunVisibility(float3 pos, float3 dirToSun) {
	if (cloudShaftStrength <= 0 || cloudDeckRadius <= 0) { return 1; }

	float t = cloudDeckRayHit(pos, dirToSun);
	if (t < 0) { return 1; }   // the sun ray never meets the deck, or the deck is behind this point

	float3 crossing = normalize(pos + dirToSun * t);
	float shadow = tex2Dlod(CloudShadowMap, float4(pointToUV(crossing), 0, 0)).r;
	return lerp(1, shadow, cloudShaftStrength);
}

#endif
