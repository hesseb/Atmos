// Cloud density model and shell geometry, shared by the raymarch and the shadow pass so both agree
// on where a cloud is. Follows Schneider (Guerrilla, HZD): Perlin-Worley base, remapped by coverage,
// eroded by detail noise, confined vertically by a per-type height gradient.

#ifndef CLOUD_COMMON_INCLUDED
#define CLOUD_COMMON_INCLUDED

#include "Assets/Scripts/Shader Common/GeoMath.hlsl"
#include "Assets/Post Processing/Effects/PBR/Clouds/Shader Common/CloudNoiseCommon.hlsl"

Texture3D<float4> CloudShapeNoise;
SamplerState samplerCloudShapeNoise;

Texture3D<float4> CloudDetailNoise;
SamplerState samplerCloudDetailNoise;

Texture2D<float4> CloudWeatherMapTex;
SamplerState samplerCloudWeatherMapTex;

Texture2D<float4> CloudWeatherMapHighTex;
SamplerState samplerCloudWeatherMapHighTex;

// Shell
float cloudInnerRadius;
float cloudOuterRadius;

// Low/mid layer density
float cloudShapeScale;
float cloudDetailScale;
float cloudDetailWeight;
float cloudDensityMultiplier;
float cloudCoverageMultiplier;
float cloudTypeBias;
float cloudBaseVariation;
float cloudBandThickness;
float cloudEdgeSoftness;
float cloudPrecipDensity;
float cloudPrecipTypePush;
float cloudPrecipBaseDrop;
float cloudShapeResolution;
float cloudDetailResolution;
float3 cloudShapeWind;
float3 cloudDetailWind;

// Rise start, rise end, fall start, fall end.
float4 cloudStratusProfile;
float4 cloudCumulusProfile;
float4 cloudCumulonimbusProfile;

float cloudWeatherOverrideOn;
float4 cloudWeatherOverride;      // coverage, precipitation, form, unused
float2 cloudHighWeatherOverride;  // coverage, form
float cloudGenusBaseOffset;

// The high band: cirrus, cirrocumulus, cirrostratus
float cloudHighStrength;
float cloudHighBase;
float cloudHighThickness;
float4 cloudHighProfile;
float cloudHighScale;
float cloudHighDensityMultiplier;
float cloudHighCoverageMultiplier;
float cloudHighStretch;
float cloudHighGranularity;
float3 cloudHighWindDir;
float3 cloudHighWind;

///////// Internal helpers /////////////

float cloudProfile(float bandHeight, float4 profile)
{
	float rise = saturate((bandHeight - profile.x) / max(1e-4, profile.y - profile.x));
	float fall = saturate((bandHeight - profile.z) / max(1e-4, profile.w - profile.z));
	return smoothstep(0, 1, rise) * (1 - smoothstep(0, 1, fall));
}

// Overlapping weights for the low, middle and high third of x, summing to 1.
float3 cloudTentWeights(float x)
{
	return saturate(float3(1 - x * 2, 1 - abs(x - 0.5) * 2, x * 2 - 1));
}

// Worley FBM weights from Schneider (GPU Pro 7 II-4), as in Hillaire's TileableVolumeNoise.
float cloudWorleyFbm(float3 octaves)
{
	return octaves.x * 0.625 + octaves.y * 0.25 + octaves.z * 0.125;
}

// Coverage remap from Schneider (SIGGRAPH 2015).
float cloudCoverageRemap(float base, float coverage)
{
	return cloudRemap(base, 1.0 - coverage, 1.0, 0.0, 1.0) * coverage;
}

float cloudHeightGradient(float bandHeight, float type)
{
	float3 profiles = float3(
		cloudProfile(bandHeight, cloudStratusProfile),
		cloudProfile(bandHeight, cloudCumulusProfile),
		cloudProfile(bandHeight, cloudCumulonimbusProfile));

	return dot(profiles, cloudTentWeights(type));
}

/// R coverage, G form, for the ice layer
float4 sampleCloudWeatherHigh(float2 uv)
{
	if (cloudWeatherOverrideOn > 0)
	{
		return float4(cloudHighWeatherOverride, 0, 0);
	}

	return CloudWeatherMapHighTex.SampleLevel(samplerCloudWeatherMapHighTex, uv, 0);
}

float cloudLod(float stepSize, float scale, float resolution)
{
	return max(0, log2(max(1, stepSize * scale * resolution)));
}

///////// Mixed helpers /////////////

// Distance to where the ray enters the sphere (0 from inside), and the length it spends inside.
float2 cloudRaySphere(float radius, float3 rayOrigin, float3 rayDir)
{
	float b = dot(rayOrigin, rayDir);
	float c = dot(rayOrigin, rayOrigin) - radius * radius;
	float d = b * b - c;

	if (d < 0)
	{
		return float2(0, 0);
	}

	float s = sqrt(d);
	float near = max(0, -b - s);
	float far = -b + s;

	if (far < 0)
	{
		return float2(0, 0);
	}

	return float2(near, far - near);
}

// Where in the shell this point sits, 0 at the cloud base and 1 at the top.
float cloudHeightFraction(float3 pos)
{
	return saturate((length(pos) - cloudInnerRadius) / max(1e-4, cloudOuterRadius - cloudInnerRadius));
}

// R coverage, G precipitation, B form, A base height.
float4 sampleCloudWeather(float2 uv)
{
	if (cloudWeatherOverrideOn > 0)
	{
		return cloudWeatherOverride;
	}

	return CloudWeatherMapTex.SampleLevel(samplerCloudWeatherMapTex, uv, 0);
}

///////// Layers (internal) /////////////

float sampleCloudHigh(float3 pos, float2 weatherUV, float shellHeight, float stepSize)
{
	if (cloudHighStrength <= 0)
	{
		return 0;
	}

	float bandHeight = (shellHeight - cloudHighBase) / max(1e-4, cloudHighThickness);
	if (bandHeight <= 0 || bandHeight >= 1)
	{
		return 0;
	}

	float4 weather = sampleCloudWeatherHigh(weatherUV);

	float coverage = saturate(weather.r * cloudHighCoverageMultiplier);
	if (coverage <= 0)
	{
		return 0;
	}

	float3 formWeights = cloudTentWeights(weather.g);
	float wFibrous = formWeights.x;
	float wGranular = formWeights.y;
	float wVeil = formWeights.z;

	float profile = cloudProfile(bandHeight, cloudHighProfile);

	float stretch = wFibrous * cloudHighStretch
					+ wGranular * 1.0
					+ wVeil * (1 + (cloudHighStretch - 1) * 0.25);

	float scale = cloudHighScale * (wFibrous * 1.0 + wGranular * 2.6 + wVeil * 0.55);

	float along = dot(pos, cloudHighWindDir);
	float3 stretchedPos = (pos - cloudHighWindDir * along) + cloudHighWindDir * (along / max(1e-3, stretch));

	float4 shape = CloudShapeNoise.SampleLevel(
		samplerCloudShapeNoise, stretchedPos * scale + cloudHighWind,
		cloudLod(stepSize, scale, cloudShapeResolution));

	float base = lerp(shape.r, cloudWorleyFbm(shape.gba), wGranular * cloudHighGranularity);

	float effectiveCoverage = saturate(coverage * (1 + wVeil * 0.9));

	float density = cloudCoverageRemap(base, effectiveCoverage);
	if (density <= 0)
	{
		return 0;
	}

	return density * profile * cloudHighDensityMultiplier * cloudHighStrength;
}

float sampleCloudLow(float3 pos, float2 weatherUV, float shellHeight, bool cheap, float stepSize)
{
	float bandCeiling = saturate(cloudWeatherOverrideOn > 0 ? cloudGenusBaseOffset : cloudBaseVariation)
		+ max(0.05, cloudBandThickness);
	if (shellHeight >= bandCeiling)
	{
		return 0;
	}

	float4 weather = sampleCloudWeather(weatherUV);
	float coverage = saturate(weather.r * cloudCoverageMultiplier);
	if (coverage <= 0)
	{
		return 0;
	}

	float precipitation = weather.g;

	float type = saturate(weather.b + cloudTypeBias + precipitation * cloudPrecipTypePush);

	float baseSource = cloudWeatherOverrideOn > 0 ? cloudGenusBaseOffset : weather.a * cloudBaseVariation;
	float baseOffset = saturate(baseSource - precipitation * cloudPrecipBaseDrop);

	float thickness = min(max(0.05, cloudBandThickness), 1.0 - baseOffset);
	float bandHeight = saturate((shellHeight - baseOffset) / max(0.05, thickness));

	float gradient = cloudHeightGradient(bandHeight, type);
	if (gradient <= 0)
	{
		return 0;
	}

	float4 shape = CloudShapeNoise.SampleLevel(
		samplerCloudShapeNoise, pos * cloudShapeScale + cloudShapeWind,
		cloudLod(stepSize, cloudShapeScale, cloudShapeResolution));

	float worleyFbm = cloudWorleyFbm(shape.gba);

	float densityGradient = lerp(gradient, 1.0, cloudEdgeSoftness);
	float coverageGradient = lerp(1.0, gradient, cloudEdgeSoftness);
	float effectiveCoverage = saturate(coverage * coverageGradient);

	// Base shape remap from Schneider (SIGGRAPH 2015).
	float base = saturate(cloudRemap(shape.r, worleyFbm - 1.0, 1.0, 0.0, 1.0)) * densityGradient;

	float density = cloudCoverageRemap(base, effectiveCoverage);
	if (density <= 0)
	{
		return 0;
	}

	density *= 1 + precipitation * cloudPrecipDensity;

	if (cheap)
	{
		return density * cloudDensityMultiplier;
	}

	float3 detail = CloudDetailNoise.SampleLevel(
		samplerCloudDetailNoise, pos * cloudDetailScale + cloudDetailWind,
		cloudLod(stepSize, cloudDetailScale, cloudDetailResolution)).rgb;
	float detailFbm = cloudWorleyFbm(detail);

	// Schneider's wispy-to-billowy flip (SIGGRAPH 2015); the ramp rate is authored.
	float erosion = lerp(1 - detailFbm, detailFbm, saturate(bandHeight * 5));
	density = cloudRemap(density, erosion * cloudDetailWeight, 1.0, 0.0, 1.0);

	return max(0, density) * cloudDensityMultiplier;
}

///////// External entry points /////////////////

bool cloudShellSegments(float3 rayOrigin, float3 rayDir, out float2 nearSpan, out float2 farSpan)
{
	nearSpan = 0;
	farSpan = 0;

	float2 outerHit = cloudRaySphere(cloudOuterRadius, rayOrigin, rayDir);
	if (outerHit.y <= 0)
	{
		return false; // never reaches the shell at all
	}

	float outerStart = outerHit.x;
	float outerEnd = outerHit.x + outerHit.y;

	float2 innerHit = cloudRaySphere(cloudInnerRadius, rayOrigin, rayDir);
	if (innerHit.y <= 0)
	{
		// Never dips below the cloud base: one span, the whole outer chord.
		nearSpan = float2(outerStart, outerEnd);
		return true;
	}

	float innerStart = innerHit.x;
	float innerEnd = innerHit.x + innerHit.y;

	// Everything inside the outer sphere but outside the inner one.
	nearSpan = float2(outerStart, min(outerEnd, innerStart));
	farSpan = float2(max(outerStart, innerEnd), outerEnd);
	return true;
}

float sampleCloudDensity(float3 pos, bool cheap, float stepSize)
{
	float shellHeight = cloudHeightFraction(pos);
	if (shellHeight <= 0 || shellHeight >= 1)
	{
		return 0;
	}

	float2 weatherUV = pointToUV(normalize(pos));


	float low = sampleCloudLow(pos, weatherUV, shellHeight, cheap, stepSize);
	float high = sampleCloudHigh(pos, weatherUV, shellHeight, stepSize);

	return max(low, high);
}

#endif
