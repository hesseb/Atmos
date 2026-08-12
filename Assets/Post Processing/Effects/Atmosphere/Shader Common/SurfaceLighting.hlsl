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
//
// ToneMapCommon rather than DrawAtmosphereCommon, which is what this used to include. The tone map
// is all this header ever wanted from it, and the rest - getBlueNoise - reads _ScreenParams and
// samples with implicit derivatives, so pulling it in made this whole file impossible to include
// from a compute shader. It is now compute-safe, which is what lets the cloud deck's radiance bake
// call these functions instead of restating them.
//
// A shader that wants blueNoiseDither must include DrawAtmosphereCommon itself. Ocean.shader and
// Clouds.shader both used to get it through here by accident, and both now say so.
#include "Assets/Post Processing/Effects/Atmosphere/Shader Common/ToneMapCommon.hlsl"
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

/// tex2Dlod, not tex2D. The map is a RenderTexture with no mip chain, so mip 0 is the only level
/// there is and the two are identical here - but tex2D needs derivatives, which do not exist in a
/// compute shader, and this header is now included by one.
float cloudShadow(float3 sphereNormal) {
	if (cloudShadowStrength <= 0) { return 1; }
	float shadow = tex2Dlod(CloudShadowMap, float4(pointToUV(sphereNormal), 0, 0)).r;
	return lerp(1, shadow, cloudShadowStrength);
}

/// What the cloud deck looks like from below, and how much sky it hides.
///
/// The counterpart to cloudShadow, and the reason it is needed: cloudShadow darkens the direct SUN
/// under cloud, but a surface is also lit by, and reflects, the sky - and those terms knew nothing
/// about cloud at all. Under full overcast at sunset the sea still reflected a clear orange horizon,
/// which at grazing incidence is most of the image, because Schlick's F0 for water is 0.02 but
/// approaches 1 exactly where a sunset is viewed from.
///
///   rgb  radiance leaving the deck's underside, already in DISPLAY space
///   a    how much of the sky it hides, 0 when clouds are off
///
/// Display space, so a consumer blends it straight into a sampleSkyViewSafe result without tone
/// mapping either - the bake assembles its colour the same way the march does, which is what makes
/// the reflected deck look like the drawn deck.
///
/// Not gated on skyReflectionStrength, and deliberately: cloudShadow is not either. The clouds are a
/// separate renderer from the sky, so they occlude it whichever sky is being drawn - and in the
/// baseline modes the deck falls back to the same authored ambient the clouds themselves use.
sampler2D CloudSkyMap;
float cloudSkyStrength;

/// Radius the deck is treated as sitting at, for the reflected-ray lookup below. Mid-shell: the map
/// describes a whole column, so any single radius is a choice.
float cloudDeckRadius;

float4 sampleCloudSky(float3 sphereNormal) {
	if (cloudSkyStrength <= 0) { return 0; }
	float4 deck = tex2Dlod(CloudSkyMap, float4(pointToUV(sphereNormal), 0, 0));
	return float4(deck.rgb, saturate(deck.a * cloudSkyStrength));
}

/// The deck where `dir` meets it, rather than the deck directly overhead.
///
/// For reflections, and the distinction is not cosmetic. A grazing reflection - which is what a
/// sunset across water is - travels a long way beneath the deck before reaching it, so the cloud it
/// meets can be tens of units away and completely unlike the sky above the viewer. Using the
/// overhead value would reflect the wrong weather in exactly the case this exists for.
float4 sampleCloudSkyAlong(float3 pos, float3 dir) {
	if (cloudSkyStrength <= 0) { return 0; }

	float3 up = normalize(pos);
	// Below the horizon there is no deck to meet along this ray; the far root would land on the far
	// side of the planet. Ocean.shader lifts its reflection above the horizon before calling, so
	// this is a guard rather than a path.
	if (dot(dir, up) <= 0) { return sampleCloudSky(up); }

	// Always the far root: a surface point is inside the deck's sphere, so there is exactly one
	// exit. Local rather than an include of AtmosphereCommon's raySphere, which owns the global
	// dirToSun and would shadow the local sun direction of every shader including this header.
	float b = dot(pos, dir);
	float c = dot(pos, pos) - cloudDeckRadius * cloudDeckRadius;
	float d = b * b - c;
	if (d < 0) { return sampleCloudSky(up); }

	float3 hit = pos + dir * (-b + sqrt(d));
	return sampleCloudSky(normalize(hit));
}

/// Blends a sky term toward the deck hiding it. Both are display space, so neither is tone mapped
/// here, and a = 0 makes this the identity - which is what happens with the clouds off.
float3 applyCloudSky(float3 skyTerm, float4 deck) {
	return lerp(skyTerm, deck.rgb, deck.a);
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

/// Transmittance of the air between two points on the same ray, as a multiplier.
///
/// This is aerial perspective's extinction term for a volume, and it exists because the screen-space
/// aerial perspective LUT cannot answer the question for clouds. That LUT's depth axis is normalised
/// per ray against a span deliberately capped at the planet's tangent distance - 55.7 units from 10
/// up - to keep the axis continuous across the horizon. The cap is justified in
/// AerialPerspective.compute on the grounds that it only truncates rays which miss the planet, "and
/// those are sky, where the composite takes its sky branch and never reads this LUT". Clouds are
/// exactly those rays: a horizon-grazing ray crosses the cloud shell again at around 105 units, well
/// past the end of the axis. Extending the span would bring the horizon band back.
///
/// So this takes the other route - Bruneton's ratio identity, the same one Hillaire 2020 uses.
/// Transmittance to space composes along a ray, so the transmittance of a SEGMENT of it is a
/// quotient of two whole-ray lookups, and the existing 2D LUT already holds both.
///
/// The case split is not optional, and it is the reason this is a shared function rather than four
/// lines at the call site. Bruneton's mapping stores only rays that miss the ground, and
/// sampleTransmittanceLUT returns exactly 0 for a below-horizon direction. Reversing the direction
/// turns a descending ray into an ascending one, so BOTH branches only ever ask the LUT about rays
/// it actually contains. Asking it directly about a downward ray returns zero and blacks the volume
/// out entirely.
///
/// Returns 1 without a physically based atmosphere, so a caller can multiply unconditionally and get
/// its previous behaviour in the baseline and ablation profiles - the same convention
/// sampleLightColour follows.
///
/// `rayDir` must be the normalised direction from the origin toward the far point; the two have to
/// lie on one ray for the identity to mean anything.
///
/// Split into a begin/at pair rather than one call, because a volume evaluates this at every march
/// step along one ray and everything about the origin is loop-invariant: the direction test, and one
/// of the two lookups. Hoisting it explicitly costs the caller two lines and halves the per-step tap
/// count, which is not something to leave to whether the compiler notices.
struct AerialRay {
	float3 originTransmittance;
	float3 dir;
	bool descending;
	bool active;
};

AerialRay aerialRayBegin(float3 origin, float3 rayDir) {
	AerialRay ray;
	ray.dir = rayDir;
	ray.active = hasPhysicalAtmosphere();

	float radius = length(origin);
	float cosZenith = dot(origin / radius, rayDir);

	// Which way the ray is headed decides which quotient is the valid one. Tested at the origin,
	// because that is where the segment starts - a ray can pass its lowest point partway along
	// without changing the answer, since the far point is still between the origin and the exit.
	ray.descending = transmittanceRayHitsGround(radius, cosZenith);

	// Descending, the reversed lookup is the denominator; ascending, the forward one is the
	// numerator. Either way it is the origin's, so it is computed once here.
	ray.originTransmittance = ray.active
		? sampleTransmittanceLUT(TransmittanceLUT, origin, ray.descending ? -rayDir : rayDir)
		: 1;
	return ray;
}

float3 aerialTransmittanceAt(AerialRay ray, float3 pos) {
	if (!ray.active) { return 1; }

	float3 atPos = sampleTransmittanceLUT(TransmittanceLUT, pos, ray.descending ? -ray.dir : ray.dir);

	float3 nearer = ray.descending ? atPos : ray.originTransmittance;
	float3 farther = ray.descending ? ray.originTransmittance : atPos;

	// saturate because the quotient is bounded by 1 analytically but not numerically, and 1e-5
	// because the denominator genuinely reaches zero for a grazing ray through the whole atmosphere.
	return saturate(nearer / max(farther, 1e-5));
}

/// The whole thing in one call, for a caller with a single point to evaluate. Defined in terms of
/// the pair above so there is still one implementation of the identity.
float3 aerialTransmittance(float3 fromPos, float3 toPos, float3 rayDir) {
	return aerialTransmittanceAt(aerialRayBegin(fromPos, rayDir), toPos);
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
