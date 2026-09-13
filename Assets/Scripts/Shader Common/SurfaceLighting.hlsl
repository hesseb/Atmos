// Lighting terms that let a surface take its colour from the physically based atmosphere
#ifndef SURFACE_LIGHTING_INCLUDED
#define SURFACE_LIGHTING_INCLUDED

#include "Assets/Post Processing/Effects/PBR/Atmosphere/Shader Common/TransmittanceCommon.hlsl"
#include "Assets/Post Processing/Effects/PBR/Atmosphere/Shader Common/SkyViewCommon.hlsl"
#include "Assets/Scripts/Shader Common/ToneMapCommon.hlsl"
#include "Assets/Scripts/Shader Common/GeoMath.hlsl"
#include "Assets/Post Processing/Effects/PBR/Atmosphere/Shader Common/CloudShadowCommon.hlsl"

sampler2D TransmittanceLUT;
float skyReflectionStrength;
sampler2D CloudSkyMap;
float cloudSkyStrength;

///////// Internal helper /////////

/// Sea level exactly. The terrain and ocean meshes are relief-corrected, so their own radius is
/// not planetRadius - and the transmittance LUT returns 0 for every downward ray below it.
float3 seaLevelPosition(float3 sphereNormal) {
	return sphereNormal * (planetRadius + 1e-3);
}

///////// Mixed helpers /////////

/// Whether the atmosphere has published its globals this frame.
bool hasPhysicalAtmosphere() {
	return planetRadius > 0 && skyReflectionStrength > 0;
}

float4 sampleCloudSky(float3 sphereNormal) {
	if (cloudSkyStrength <= 0)
	{
		return 0;
	}

	float4 deck = tex2Dlod(CloudSkyMap, float4(pointToUV(sphereNormal), 0, 0));
	return float4(deck.rgb, saturate(deck.a * cloudSkyStrength));
}

/// Transmittance of the air between two points on the same ray, as a multiplier.
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

	ray.descending = transmittanceRayHitsGround(radius, cosZenith);

	ray.originTransmittance = ray.active
		? sampleTransmittanceLUT(TransmittanceLUT, origin, ray.descending ? -rayDir : rayDir)
		: 1;

	return ray;
}

float3 aerialTransmittanceAt(AerialRay ray, float3 pos) {
	if (!ray.active)
	{
		return 1;
	}

	float3 atPos = sampleTransmittanceLUT(TransmittanceLUT, pos, ray.descending ? -ray.dir : ray.dir);

	float3 nearer = ray.descending ? atPos : ray.originTransmittance;
	float3 farther = ray.descending ? ray.originTransmittance : atPos;

	return saturate(nearer / max(farther, 1e-5));
}

///////// External API /////////

/// The colour a light source has by the time it reaches the surface.
float3 sampleLightColour(float3 sphereNormal, float3 lightDir) {
	if (!hasPhysicalAtmosphere())
	{
		return 1;
	}

	return sampleTransmittanceLUT(TransmittanceLUT, seaLevelPosition(sphereNormal), lightDir);
}

/// The same at a point's own altitude rather than sea level. 
float3 sampleLightColourAt(float3 pos, float3 lightDir) {
	if (!hasPhysicalAtmosphere()) { return 1; }
	return sampleTransmittanceLUT(TransmittanceLUT, pos, lightDir);
}

/// The deck where `dir` meets it, rather than the deck directly overhead.
float4 sampleCloudSkyAlong(float3 pos, float3 dir) {
	if (cloudSkyStrength <= 0)
	{
		return 0;
	}

	float3 up = normalize(pos);

	if (dot(dir, up) <= 0)
	{
		return sampleCloudSky(up);
	}

	float t = cloudDeckRayHit(pos, dir);

	if (t < 0)
	{
		return sampleCloudSky(up);
	}

	return sampleCloudSky(normalize(pos + dir * t));
}

/// Blends a sky term toward the deck hiding it.
float3 applyCloudSky(float3 skyTerm, float4 deck) {
	return lerp(skyTerm, deck.rgb, deck.a);
}

/// Sky radiance in a direction
float3 sampleSkyViewSafe(float3 up, float3 dir, float3 sunDir) {
	if (!hasPhysicalAtmosphere())
	{
		return 0;
	}

	return max(0, toneMap(sampleSkyView(up, dir, sunDir))) * skyReflectionStrength;
}

#endif
