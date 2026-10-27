// The cloud's optical model - phase function, Beer-Powder, cone-sampled light march, ambient and step integration

#ifndef CLOUD_LIGHTING_COMMON_INCLUDED
#define CLOUD_LIGHTING_COMMON_INCLUDED

#include "Assets/Post Processing/Effects/PBR/Clouds/Shader Common/CloudCommon.hlsl"
#include "Assets/Scripts/Shader Common/SurfaceLighting.hlsl"

float3 cloudSunDir;
float3 cloudSunColour;
float cloudSunIntensity;
float cloudSilverIntensity;
float cloudAmbientIntensity;
float cloudAmbientHorizon;
float3 cloudAmbientFallback;
float3 cloudNightAmbient;
float cloudExtinction;

float4 moonPosition;
float4 moonLightColour;
float cloudMoonIntensity;
float cloudMoonSilverIntensity;

float cloudLightMarchLength;
float cloudLightAbsorption;
float cloudConeSpread;
float cloudLightFarDistance;
float cloudLightMarchDetail;
float cloudPowderStrength;
float cloudPhaseForward;
float cloudPhaseBackward;
float cloudPhaseBlend;
float cloudSilverSpread;
float cloudLightTransmittanceWeight;

///////// Internal helpers /////////////

float cloudHenyeyGreenstein(float cosAngle, float g)
{
	float g2 = g * g;
	return (1 - g2) / (4 * PI * pow(max(1e-3, 1 + g2 - 2 * g * cosAngle), 1.5));
}

///////// External entry points /////////////////

// Two-lobe Henyey-Greenstein (forward + backward), scaled by 4*pi so isotropic evaluates to 1, plus Schneider's silver lining
float cloudPhase(float cosAngle, float silverIntensity)
{
	float forward = cloudHenyeyGreenstein(cosAngle, cloudPhaseForward);
	float backward = cloudHenyeyGreenstein(cosAngle, -cloudPhaseBackward);
	float base = lerp(forward, backward, cloudPhaseBlend) * 4 * PI;

	float silverG = 0.99 - cloudSilverSpread;
	float silver = silverIntensity * cloudHenyeyGreenstein(cosAngle, silverG) / max(1e-4, cloudHenyeyGreenstein(1, silverG));

	return max(base, silver);
}

// Schneider's light samples (SIGGRAPH 2015)
// The cone layout is new, being evenly spaced
float cloudLightMarch(float3 pos, float3 lightDir)
{
	const int coneSteps = 5;
	// `cheap` skips detail erosion
	const bool cheap = true;
	float stepSize = cloudLightMarchLength / coneSteps;

	float3 tangent = normalize(cross(lightDir, abs(lightDir.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0)));
	float3 bitangent = cross(lightDir, tangent);

	float totalDensity = 0;

	[unroll]
	for (int i = 0; i < coneSteps; i++)
	{
		float along = (i + 0.5) * stepSize;
		float angle = i * (2 * PI / coneSteps);
		float3 offset = (tangent * cos(angle) + bitangent * sin(angle)) * (cloudConeSpread * along);
		float3 samplePos = pos + lightDir * along + offset;
		totalDensity += max(0, sampleCloudDensity(samplePos, cheap, cloudLightMarchDetail)) * stepSize;
	}

	float3 farPos = pos + lightDir * (cloudLightMarchLength * cloudLightFarDistance);
	totalDensity += max(0, sampleCloudDensity(farPos, cheap, cloudLightMarchDetail)) * stepSize;

	return totalDensity;
}

/// A light's colour at a sample, desaturated toward its own luminance by cloudLightTransmittanceWeight.
float3 cloudLightColour(float3 transmittance, float3 colour)
{
	// Rec. 709 luminance weights.
	float grey = dot(transmittance, float3(0.2126, 0.7152, 0.0722));
	return lerp(grey.xxx, transmittance, cloudLightTransmittanceWeight) * colour;
}

/// Beer-Lambert with a powder term for the dark edge of a thin volume
float cloudBeerPowder(float opticalDepth, float towardSun)
{
	float beer = exp(-opticalDepth * cloudLightAbsorption);
	float powder = 1 - exp(-opticalDepth * cloudLightAbsorption * 2);
	float strength = cloudPowderStrength * saturate(0.5 - 0.5 * towardSun);
	return beer * lerp(1.0, powder, strength);
}

/// Direct sunlight scattered toward the viewer at a sample
float3 cloudSunInScatter(float3 pos, float cosViewSun, float phase)
{
	float3 sunColour = cloudLightColour(sampleLightColourAt(pos, cloudSunDir), cloudSunColour);
	float sunEnergy = cloudBeerPowder(cloudLightMarch(pos, cloudSunDir), cosViewSun);
	return sunColour * sunEnergy * phase * cloudSunIntensity;
}

/// moonLightColour arrives scaled by the moon's phase, so a new moon is inactive
bool cloudMoonActive()
{
	return dot(moonLightColour.rgb, 1) > 1e-4;
}

/// Moonlight scattered toward the viewer at a sample, with the sun's model
float3 cloudMoonInScatter(float3 pos, float3 viewDir)
{
	float3 moonDir = normalize(moonPosition.xyz - pos);
	float cosViewMoon = dot(viewDir, moonDir);

	float3 moonColour = cloudLightColour(sampleLightColourAt(pos, moonDir), moonLightColour.rgb);
	float moonEnergy = cloudBeerPowder(cloudLightMarch(pos, moonDir), cosViewMoon);
	float moonPhase = cloudPhase(cosViewMoon, cloudMoonSilverIntensity);

	return moonColour * moonEnergy * moonPhase * cloudMoonIntensity;
}

/// Skylight on a cloud, from the zenith and from the horizon under the sun, so a sunset's orange carries onto it
float3 cloudSkyAmbient(float3 up)
{
	float3 sunTangent = cloudSunDir - up * dot(cloudSunDir, up);
	float tangentLength = length(sunTangent);
	float3 horizonDir = tangentLength > 1e-3 ? sunTangent / tangentLength : up;

	float3 skyZenith = sampleSkyViewSafe(up, up, cloudSunDir);
	float3 skyHorizon = sampleSkyViewSafe(up, horizonDir, cloudSunDir);

	float3 ambient = lerp(skyZenith, skyHorizon, cloudAmbientHorizon) * cloudAmbientIntensity;
	return ambient + cloudAmbientFallback * (hasPhysicalAtmosphere() ? 0.0 : 1.0);
}

/// Authored floor for the night side, faded in just past sunset
float3 cloudNightAmbientAt(float3 up)
{
	float nightFactor = saturate(dot(up, -cloudSunDir));
	return cloudNightAmbient * smoothstep(0.0, 0.15, nightFactor);
}

/// Analytic per-step integration: for a purely scattering medium the scattering and extinction cancel, leaving 1 - stepTransmittance
void cloudIntegrateStep(float3 inScatter, float density, float stepSize, inout float transmittance, inout float3 luminance)
{
	float stepTransmittance = exp(-density * stepSize * cloudExtinction);
	luminance += inScatter * (1 - stepTransmittance) * transmittance;
	transmittance *= stepTransmittance;
}

#endif
