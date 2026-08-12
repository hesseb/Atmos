// How much sunlight the cloud deck lets through, for consumers on the ground and in the air.
//
// Its own header because BOTH SurfaceLighting.hlsl and AtmosphereCommon.hlsl need it, and those two
// cannot include one another: AtmosphereCommon owns the global `dirToSun`, and pulling it into
// SurfaceLighting would shadow the local sun direction of every forward shader that includes it -
// which is why SurfaceLighting's own header comment says AtmosphereCommon is "deliberately absent".
// Declaring the map in both would then collide the moment one shader wanted both. Same split, and
// the same reason, as ToneMapCommon.hlsl.
//
// Two questions, not one, and they are genuinely different:
//
//   cloudShadow          how much sun reaches a point on the GROUND, projected radially
//   cloudSunVisibility   how much sun reaches a point in the AIR, along the actual sun ray
//
// The second is what produces light shafts, and it is why the first is not simply reused: a surface
// point and the air above it do not see the same cloud once the sun is low.

#ifndef CLOUD_SHADOW_COMMON_INCLUDED
#define CLOUD_SHADOW_COMMON_INCLUDED

#include "Assets/Scripts/Shader Common/GeoMath.hlsl"

/// Published by whichever cloud renderer is drawing - the volumetric or the baseline. Both write the
/// same globals, so no consumer can tell which produced them.
///
/// Equirectangular and indexed by pointToUV like everything else on this globe, which is why it
/// costs one tap and no new plumbing: it is view-independent, needs no shadow frustum, and does not
/// change when the camera moves.
sampler2D CloudShadowMap;

/// Surfaces. 0 when the clouds are off.
float cloudShadowStrength;

/// The air. Separate from the surface strength on purpose - they are two features with two very
/// different costs, one a tap per pixel and one a tap per raymarch step, and RQ2 wants them priced
/// apart. 0 is exactly identity.
float cloudShaftStrength;

/// The radius the deck is treated as sitting at. 0 before any cloud renderer has published, which
/// cloudSunVisibility reads as "no deck" rather than as a deck at the planet's centre.
float cloudDeckRadius;

/// Sunlight reaching the globe after passing through the cloud shell, as a multiplier. 1 where there
/// is no cloud overhead, and 1 outright when clouds are off.
///
/// Deliberately NOT folded into sampleLightColour: that function is also called with the moon's
/// direction, and this map is baked for the sun. The call sites apply it to their sun terms only.
///
/// tex2Dlod, not tex2D. The map is a RenderTexture with no mip chain, so mip 0 is the only level
/// there is and the two are identical here - but tex2D needs derivatives, which do not exist in a
/// compute shader, and this header is included by several.
float cloudShadow(float3 sphereNormal) {
	if (cloudShadowStrength <= 0) { return 1; }
	float shadow = tex2Dlod(CloudShadowMap, float4(pointToUV(sphereNormal), 0, 0)).r;
	return lerp(1, shadow, cloudShadowStrength);
}

/// Sunlight reaching a point IN THE AIR, after the deck. This is the term that makes light shafts.
///
/// Not cloudShadow with a different argument. That one answers the question for a point on the
/// ground by projecting radially, which is only right with the sun overhead; here the answer is
/// taken where the sun ray from this point actually crosses the deck, which is what gives the beams
/// their lean and is most of what makes them read as sunlight rather than as a projected texture.
///
/// There is deliberately NO altitude cutoff. The obvious guard - above the deck, return 1 - is wrong
/// at exactly the moment shafts matter: with the sun low, a ray reaching a point above the deck
/// still crosses it at a shallow angle a long way off. Points genuinely clear of the deck fall out
/// of the intersection on their own, because their sun ray misses the sphere.
float cloudSunVisibility(float3 pos, float3 dirToSun) {
	if (cloudShaftStrength <= 0 || cloudDeckRadius <= 0) { return 1; }

	float b = dot(pos, dirToSun);
	float c = dot(pos, pos) - cloudDeckRadius * cloudDeckRadius;
	float d = b * b - c;
	if (d < 0) { return 1; }   // the sun ray never meets the deck

	float s = sqrt(d);
	// Inside the deck's sphere there is one crossing ahead, the exit. Outside it, the near root is
	// where the ray enters. c carries the sign that decides which, so this needs no altitude test.
	float t = (c < 0) ? (-b + s) : (-b - s);
	if (t < 0) { return 1; }   // the deck is behind this point, not between it and the sun

	float3 crossing = normalize(pos + dirToSun * t);
	float shadow = tex2Dlod(CloudShadowMap, float4(pointToUV(crossing), 0, 0)).r;
	return lerp(1, shadow, cloudShaftStrength);
}

#endif
