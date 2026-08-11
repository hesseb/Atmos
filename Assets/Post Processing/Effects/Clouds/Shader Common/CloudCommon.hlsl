// The cloud density model, and the shell geometry the march walks through.
//
// One definition, shared by the raymarch and (from stage 5) the shadow pass, so the two can never
// disagree about where a cloud is - the same rule TransmittanceCommon.hlsl states for the same
// reason: a mismatch produces a plausible image rather than an obvious fault.
//
// Follows Schneider (Guerrilla, "The Real-Time Volumetric Cloudscapes of Horizon Zero Dawn"):
// Perlin-Worley base, remapped by a coverage field, eroded by higher-frequency detail, and confined
// vertically by a height gradient chosen per cloud type.

#ifndef CLOUD_COMMON_INCLUDED
#define CLOUD_COMMON_INCLUDED

// Assets-absolute, never relative: a nested relative include resolves against the *including*
// file's directory, which is what turned the ocean pink the first time.
#include "Assets/Scripts/Shader Common/GeoMath.hlsl"
#include "Assets/Post Processing/Effects/Clouds/Shader Common/CloudNoiseCommon.hlsl"

// ---------------------------------------------------------------------------------------------
// Resources
// ---------------------------------------------------------------------------------------------

// Texture3D + SamplerState rather than sampler3D/tex3Dlod, because this header is included by a
// compute shader as well as a fragment shader and tex3Dlod does not exist there. The reference
// project uses the same form in its fragment shader, so it is known to compile in a CGPROGRAM.
Texture3D<float4> CloudShapeNoise;
SamplerState samplerCloudShapeNoise;

Texture3D<float4> CloudDetailNoise;
SamplerState samplerCloudDetailNoise;

Texture2D<float4> CloudWeatherMapTex;
SamplerState samplerCloudWeatherMapTex;

// ---------------------------------------------------------------------------------------------
// Shell
// ---------------------------------------------------------------------------------------------

/// Sea level plus the cloud base / top. Published by CloudEffect rather than derived from the
/// atmosphere's planetRadius, because that global is zero whenever the atmosphere is off - and the
/// clouds are a separate renderer that has to keep working in the baseline and ablation profiles.
float cloudInnerRadius;
float cloudOuterRadius;

/// Deliberately a local copy of AtmosphereCommon's raySphere rather than an include of it.
///
/// AtmosphereCommon owns the global `dirToSun` and would shadow this shader's own sun direction -
/// the same collision SurfaceLighting.hlsl avoids the same way. Re-homing raySphere into a shared
/// header would be tidier, but it would edit a file five working computes include, and Unity does
/// not track .hlsl changes as .compute import dependencies, so every one of them would need a
/// manual reimport to avoid running stale. Not worth it for twenty lines. Worth revisiting if a
/// third consumer appears.
///
/// Returns (distance to the sphere, distance through it). x is 0 when already inside; y is 0 on a
/// miss.
float2 cloudRaySphere(float radius, float3 rayOrigin, float3 rayDir)
{
	float b = dot(rayOrigin, rayDir);
	float c = dot(rayOrigin, rayOrigin) - radius * radius;
	float d = b * b - c;

	if (d < 0) { return float2(0, 0); }

	float s = sqrt(d);
	float near = max(0, -b - s);
	float far = -b + s;

	if (far < 0) { return float2(0, 0); }
	return float2(near, far - near);
}

/// The span of this ray that lies inside the cloud shell.
///
/// All three camera cases are handled explicitly rather than left to fall out, because a free-fly
/// camera finds the broken one immediately and the failure - clouds vanishing or filling the screen
/// as you cross a boundary - is not subtle.
bool cloudShellSegment(float3 rayOrigin, float3 rayDir, out float start, out float end)
{
	start = 0;
	end = 0;

	float2 outerHit = cloudRaySphere(cloudOuterRadius, rayOrigin, rayDir);
	if (outerHit.y <= 0) { return false; }   // never reaches the shell at all

	float2 innerHit = cloudRaySphere(cloudInnerRadius, rayOrigin, rayDir);
	float radius = length(rayOrigin);

	if (radius > cloudOuterRadius)
	{
		// Above the shell - the strategy viewpoint. Enter at the outer sphere, and leave either at
		// the inner sphere or, for a ray that passes over the top of it, back out through the outer.
		start = outerHit.x;
		end = (innerHit.y > 0) ? innerHit.x : outerHit.x + outerHit.y;
	}
	else if (radius > cloudInnerRadius)
	{
		// Inside the shell. Start at the camera; leave at whichever sphere comes first.
		start = 0;
		end = (innerHit.y > 0 && innerHit.x > 0) ? innerHit.x : outerHit.y;
	}
	else
	{
		// Below the shell, looking up. Both hits have x == 0 because we are inside both spheres, so
		// the shell begins where the inner sphere is exited.
		start = innerHit.y;
		end = outerHit.y;
	}

	return end > start;
}

/// Where in the shell this point sits, 0 at the cloud base and 1 at the top.
float cloudHeightFraction(float3 pos)
{
	return saturate((length(pos) - cloudInnerRadius) / max(1e-4, cloudOuterRadius - cloudInnerRadius));
}

// ---------------------------------------------------------------------------------------------
// Density
// ---------------------------------------------------------------------------------------------

float cloudShapeScale;
float cloudDetailScale;
float cloudDetailWeight;
float cloudDensityMultiplier;
float cloudCoverageMultiplier;
float cloudTypeBias;

/// Texels per axis in each volume, so a march step can be converted into a mip level.
float cloudShapeResolution;
float cloudDetailResolution;

float3 cloudShapeWind;
float3 cloudDetailWind;

/// The vertical profile, blended between three genera.
///
/// This is the control RQ1 scores. THESIS.md makes the cloud-genera table the rubric - "which
/// genera can each renderer produce, and are they recognisable" - so the cloud-type axis has to
/// move the silhouette, not just the density. Stratus is a thin sheet hugging the base; cumulus
/// occupies the middle with room to billow; cumulonimbus reaches nearly the whole shell.
float cloudHeightGradient(float h, float type)
{
	float stratus = saturate(cloudRemap(h, 0.00, 0.05, 0, 1)) * saturate(cloudRemap(h, 0.12, 0.22, 1, 0));
	float cumulus = saturate(cloudRemap(h, 0.00, 0.18, 0, 1)) * saturate(cloudRemap(h, 0.55, 0.85, 1, 0));
	float cumulonimbus = saturate(cloudRemap(h, 0.00, 0.10, 0, 1)) * saturate(cloudRemap(h, 0.85, 1.00, 1, 0));

	// Tent weights so type sweeps stratus -> cumulus -> cumulonimbus across [0,1] with only two
	// profiles ever active at once.
	float wStratus = saturate(1 - type * 2);
	float wCumulus = saturate(1 - abs(type - 0.5) * 2);
	float wCumulonimbus = saturate(type * 2 - 1);

	return stratus * wStratus + cumulus * wCumulus + cumulonimbus * wCumulonimbus;
}

float3 sampleCloudWeather(float3 pos)
{
	float2 uv = pointToUV(normalize(pos));
	return CloudWeatherMapTex.SampleLevel(samplerCloudWeatherMapTex, uv, 0).rgb;
}

/// Mip level at which a volume should be read, given how far the march moves between samples.
///
/// Point-sampling a noise volume at steps wider than its features does not merely lose detail, it
/// makes the density DEPEND ON THE STEP SIZE - and the step size varies with view angle, because
/// the step count is capped while the traversed segment is not. A grazing ray through this shell
/// steps roughly fourteen times further than a vertical one, far enough to skip whole detail
/// features, so cloud appeared to pop into existence as the camera closed and the step shrank.
///
/// Reading a mip whose texels are as wide as the step makes each sample an average of what it
/// would otherwise have jumped over, which is what mips are for and what Schneider uses for
/// distant samples. The result is stable under camera motion rather than merely more detailed.
float cloudLod(float stepSize, float scale, float resolution)
{
	// A volume tiles every 1/scale world units across `resolution` texels, so one texel spans
	// 1/(scale*resolution) world units.
	return max(0, log2(max(1, stepSize * scale * resolution)));
}

/// Cloud density at a world position.
///
/// `cheap` skips the detail erosion, which is the expensive half - used by the light march, where
/// the extra fidelity is invisible. `stepSize` is how far the caller moves between samples, and
/// selects the mip - see cloudLod.
float sampleCloudDensity(float3 pos, bool cheap, float stepSize)
{
	float h = cloudHeightFraction(pos);
	if (h <= 0 || h >= 1) { return 0; }

	float3 weather = sampleCloudWeather(pos);
	float coverage = saturate(weather.r * cloudCoverageMultiplier);
	if (coverage <= 0) { return 0; }

	float type = saturate(weather.b + cloudTypeBias);

	float gradient = cloudHeightGradient(h, type);
	if (gradient <= 0) { return 0; }

	float4 shape = CloudShapeNoise.SampleLevel(
		samplerCloudShapeNoise, pos * cloudShapeScale + cloudShapeWind,
		cloudLod(stepSize, cloudShapeScale, cloudShapeResolution));

	// Worley FBM from G/B/A, weights halving with frequency.
	float worleyFbm = shape.g * 0.625 + shape.b * 0.25 + shape.a * 0.125;

	// R is already Perlin-Worley; pulling its floor down to the Worley FBM is what gives the base
	// rounded lumps instead of smooth blobs.
	float base = saturate(cloudRemap(shape.r, worleyFbm - 1.0, 1.0, 0.0, 1.0)) * gradient;

	// Coverage carves the field rather than scaling it: more coverage lowers the threshold the base
	// has to clear, so cloud grows outward from where it already is instead of fading up uniformly.
	float density = cloudRemap(base, 1.0 - coverage, 1.0, 0.0, 1.0) * coverage;
	if (density <= 0) { return 0; }
	if (cheap) { return density * cloudDensityMultiplier; }

	float3 detail = CloudDetailNoise.SampleLevel(
		samplerCloudDetailNoise, pos * cloudDetailScale + cloudDetailWind,
		cloudLod(stepSize, cloudDetailScale, cloudDetailResolution)).rgb;
	float detailFbm = detail.r * 0.625 + detail.g * 0.25 + detail.b * 0.125;

	// Erosion inverts with height: wispy and torn at the base, billowy at the top. That asymmetry
	// is what reads as a cauliflower top over a ragged underside rather than as uniform fluff.
	float erosion = lerp(1 - detailFbm, detailFbm, saturate(h * 5));
	density = cloudRemap(density, erosion * cloudDetailWeight, 1.0, 0.0, 1.0);

	return max(0, density) * cloudDensityMultiplier;
}

#endif
