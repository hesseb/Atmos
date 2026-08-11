// Lighting terms that let an ordinary forward-rendered surface take its colour from the physically
// based atmosphere: the colour a light source has after travelling through the air to reach it, and
// the radiance of the sky above it.
//
// Shared by Ocean.shader and Terrain.shader. Both need the same three guards, and getting any of
// them wrong fails quietly rather than loudly - which is the whole reason this is one definition
// instead of two copies:
//
//   1. The sea-level sampling position. Neither mesh sits at exactly planetRadius.
//   2. The planetRadius sentinel for "no atmosphere has published its globals yet".
//   3. max(0) around toneMap, whose output at zero is about -0.05, not 0.
//
// Each is explained at the point it is applied.

#ifndef SURFACE_LIGHTING_INCLUDED
#define SURFACE_LIGHTING_INCLUDED

// Assets-absolute, not relative: these are included from Assets/Scripts/Shaders/Game, outside the
// atmosphere tree, and a nested relative include resolves against the *includer's* directory. That
// is what turned the ocean pink the first time.
#include "Assets/Post Processing/Effects/Atmosphere/Shader Common/TransmittanceCommon.hlsl"
#include "Assets/Post Processing/Effects/Atmosphere/Shader Common/SkyViewCommon.hlsl"
// For toneMap. The sky is written to the colour buffer already tone-mapped, so anything derived
// from raw sky radiance has to go through the same transform to sit at the same exposure as the
// sky one pixel away.
#include "Assets/Post Processing/Effects/Atmosphere/Shader Common/DrawAtmosphereCommon.hlsl"
// For pointToUV, used by the cloud shadow lookup. Both consumers already include it, but relying on
// their include order to satisfy this header would be a trap for the next one.
#include "Assets/Scripts/Shader Common/GeoMath.hlsl"

// Deliberately absent: AtmosphereCommon.hlsl. It is the only declaration of the global `dirToSun`,
// and including it here would shadow the local sun direction of every forward shader that includes
// this header. That is why the functions below take the sun direction as a parameter.

sampler2D TransmittanceLUT;

// 1 when the physically based sky is the one being drawn, 0 otherwise. Published by
// RenderingManager. Without it the baseline and null sky modes would light their surfaces from
// whatever the LUTs last held during a physically based frame - a frozen sky, which is not a
// defensible control condition for the comparison.
float skyReflectionStrength;

/// Whether the atmosphere has published its globals this frame. planetRadius is zero before
/// AtmosphereEffect initialises and in the baseline and null sky modes; an unbound LUT samples
/// black, so without this check the "physical" terms would come back black rather than absent.
bool hasPhysicalAtmosphere() {
	return planetRadius > 0 && skyReflectionStrength > 0;
}

/// A canonical position at sea level directly below `sphereNormal`.
///
/// Both the terrain and the ocean mesh are relief-corrected, so their radius is not exactly
/// planetRadius - and transmittanceRayHitsGround returns true for *every* downward direction once
/// radius < planetRadius, which returns exactly zero and blacks the light out at sunset, the one
/// moment these terms exist for. Sampling at sea level also keeps the camera-to-surface segment
/// the business of the aerial perspective post-process, so it is counted once rather than twice.
float3 seaLevelPosition(float3 sphereNormal) {
	return sphereNormal * (planetRadius + 1e-3);
}

/// The colour a light source has by the time it reaches the surface, as a multiplier on that
/// light's own colour: white overhead, deep orange at the horizon.
///
/// Returns 1 rather than 0 when there is no physically based atmosphere, so a caller can multiply
/// unconditionally and get its previous, uncoloured behaviour in the baseline modes.
///
/// Works for the moon as well as the sun - the air does not care which one it is.
float3 sampleLightColour(float3 sphereNormal, float3 lightDir) {
	if (!hasPhysicalAtmosphere()) { return 1; }
	return sampleTransmittanceLUT(TransmittanceLUT, seaLevelPosition(sphereNormal), lightDir);
}

/// Sunlight reaching the globe after passing through the cloud shell, as a multiplier. 1 where
/// there is no cloud overhead, and 1 outright when clouds are off.
///
/// Published by CloudEffect from Camera.onPreCull, so it is current by the time forward opaque
/// runs. Equirectangular and indexed the same way as everything else on this globe, which is why it
/// costs one tap and no new plumbing.
///
/// Deliberately NOT folded into sampleLightColour: that function is also called with the moon's
/// direction, and this map is baked for the sun. The call sites apply it to their sun terms only.
sampler2D CloudShadowMap;
float cloudShadowStrength;

float cloudShadow(float3 sphereNormal) {
	if (cloudShadowStrength <= 0) { return 1; }
	float shadow = tex2D(CloudShadowMap, pointToUV(sphereNormal)).r;
	return lerp(1, shadow, cloudShadowStrength);
}

/// The same, but at the position given rather than at sea level beneath it.
///
/// For volumes rather than surfaces. A cloud sits at altitude and that is the whole point: its top
/// sees the sun through measurably less air than the ground below it does, which is what makes a
/// cloud top stay bright while its base and the land around it have already gone red.
///
/// Surfaces must NOT use this - see seaLevelPosition for why sampling at the fragment's own radius
/// blacks their sun out at sunset.
float3 sampleLightColourAt(float3 pos, float3 lightDir) {
	if (!hasPhysicalAtmosphere()) { return 1; }
	return sampleTransmittanceLUT(TransmittanceLUT, pos, lightDir);
}

/// Sky radiance arriving from `dir` at a surface whose local up is `up`, tone-mapped to sit at the
/// same exposure as the sky the sky pass wrote into the colour buffer.
///
/// Returns 0 when there is no physically based atmosphere - this is an added term, so absent means
/// zero, unlike sampleLightColour above which is a multiplier.
float3 sampleSkyViewSafe(float3 up, float3 dir, float3 sunDir) {
	if (!hasPhysicalAtmosphere()) { return 0; }
	// max(0): toneMap(0) is about -0.05, because the contrast pivot is a pedestal. Without this the
	// sky terms SUBTRACT at night, when the LUT is genuinely zero - so an already black surface was
	// being pushed below black rather than merely left alone.
	return max(0, toneMap(sampleSkyView(up, dir, sunDir))) * skyReflectionStrength;
}

#endif
