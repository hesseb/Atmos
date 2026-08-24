// Cloud density model and shell geometry, shared by the raymarch and the shadow pass so both agree
// on where a cloud is. Follows Schneider (Guerrilla, HZD): Perlin-Worley base, remapped by coverage,
// eroded by detail noise, confined vertically by a per-type height gradient.

#ifndef CLOUD_COMMON_INCLUDED
#define CLOUD_COMMON_INCLUDED

// Assets-absolute, never relative: a nested relative include resolves against the including file's
// own directory, not this one.
#include "Assets/Scripts/Shader Common/GeoMath.hlsl"
#include "Assets/Post Processing/Effects/PBR/Clouds/Shader Common/CloudNoiseCommon.hlsl"

// ---------------------------------------------------------------------------------------------
// Resources
// ---------------------------------------------------------------------------------------------

// Texture3D + SamplerState rather than sampler3D/tex3Dlod, since this header is also included by
// compute shaders, where tex3Dlod doesn't exist.
Texture3D<float4> CloudShapeNoise;
SamplerState samplerCloudShapeNoise;

Texture3D<float4> CloudDetailNoise;
SamplerState samplerCloudDetailNoise;

Texture2D<float4> CloudWeatherMapTex;
SamplerState samplerCloudWeatherMapTex;

/// The ice layer's weather: R coverage, G form. Its own field since high cloud arrives independently
/// of the cumulus below it.
Texture2D<float4> CloudWeatherMapHighTex;
SamplerState samplerCloudWeatherMapHighTex;

// ---------------------------------------------------------------------------------------------
// Shell
// ---------------------------------------------------------------------------------------------

/// Sea level plus the cloud base / top. Published by CloudEffect rather than derived from the
/// atmosphere's planetRadius, which is zero whenever the atmosphere is off.
float cloudInnerRadius;
float cloudOuterRadius;

/// Local copy of AtmosphereCommon's raySphere, not an include - that header's `dirToSun` global
/// would shadow this shader's own sun direction.
///
/// Returns (distance to the sphere, distance through it). x is 0 when already inside; y is 0 on a miss.
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

/// The spans of this ray that lie inside the cloud shell - there can be TWO, since a ray leaving the
/// shell downward dips below the cloud base and climbs back through it further along the curved
/// planet; dropping that second span leaves a hard seam at the horizon. Expressed as the outer
/// sphere's chord minus the inner sphere's, so above/inside/below-shell cases all fall out of one
/// subtraction. An empty span comes back with end <= start and is skipped by the caller.
bool cloudShellSegments(float3 rayOrigin, float3 rayDir, out float2 nearSpan, out float2 farSpan)
{
	nearSpan = 0;
	farSpan = 0;

	float2 outerHit = cloudRaySphere(cloudOuterRadius, rayOrigin, rayDir);
	if (outerHit.y <= 0) { return false; }   // never reaches the shell at all

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

/// How much of the shell the cloud band can be lifted by, as a fraction of it.
float cloudBaseVariation;

/// How thick the cloud band is, as a fraction of the shell. The band translates up and down rather
/// than stretching to meet the shell ceiling, so its thickness stays independent of where it sits.
float cloudBandThickness;

/// How much of the height gradient acts on coverage rather than density. At 0 (Schneider's
/// arrangement) the layer ends abruptly in a flat plane; moving toward 1 lowers the coverage
/// threshold progressively instead, breaking the edge up into thinning, scattered cloud.
float cloudEdgeSoftness;

/// What precipitation does: rain-bearing cloud is thicker, taller and hangs lower, all driven by one field.
float cloudPrecipDensity;
float cloudPrecipTypePush;
float cloudPrecipBaseDrop;

/// Texels per axis in each volume, so a march step can be converted into a mip level.
float cloudShapeResolution;
float cloudDetailResolution;

float3 cloudShapeWind;
float3 cloudDetailWind;

/// The vertical profile of the low/mid layer along the FORM axis (sheet/heap/tower), in the layer's
/// own normalised height. Altitude band is a separate axis, decided by the base offset in
/// sampleCloudDensity - together they cover THESIS.md 3.5's two-dimensional genera table.
float cloudHeightGradient(float h, float type)
{
	// smoothstep rather than a linear remap, to avoid a visible crease where a linear ramp meets its plateau.
	float stratus = smoothstep(0.00, 0.05, h) * (1 - smoothstep(0.12, 0.22, h));
	float cumulus = smoothstep(0.00, 0.18, h) * (1 - smoothstep(0.55, 0.85, h));
	float cumulonimbus = smoothstep(0.00, 0.10, h) * (1 - smoothstep(0.85, 1.00, h));

	// Tent weights so type sweeps stratus -> cumulus -> cumulonimbus across [0,1] with only two
	// profiles ever active at once.
	float wStratus = saturate(1 - type * 2);
	float wCumulus = saturate(1 - abs(type - 0.5) * 2);
	float wCumulonimbus = saturate(type * 2 - 1);

	return stratus * wStratus + cumulus * wCumulus + cumulonimbus * wCumulonimbus;
}

/// Pins every weather field to one genus globally, bypassing the maps. Authoring scaffolding, not a
/// rendering path - feeds the same density model, so RQ1's per-genus reference images are genuine,
/// and the mapping itself (CloudEffect.GenusSettings) is evidence for RQ3's authoring-flexibility axis.
float cloudWeatherOverrideOn;
float4 cloudWeatherOverride;      // coverage, precipitation, form, unused
float2 cloudHighWeatherOverride;  // coverage, form

/// The low layer's floor when overriding, bypassing weather.a * cloudBaseVariation so a genus lands
/// in its stated altitude band regardless of base variation tuning. Precipitation still lowers it.
float cloudGenusBaseOffset;

/// R coverage, G precipitation, B form, A base height.
float4 sampleCloudWeather(float3 pos)
{
	if (cloudWeatherOverrideOn > 0) { return cloudWeatherOverride; }
	float2 uv = pointToUV(normalize(pos));
	return CloudWeatherMapTex.SampleLevel(samplerCloudWeatherMapTex, uv, 0);
}

/// R coverage, G form, for the ice layer.
float4 sampleCloudWeatherHigh(float3 pos)
{
	if (cloudWeatherOverrideOn > 0) { return float4(cloudHighWeatherOverride, 0, 0); }
	float2 uv = pointToUV(normalize(pos));
	return CloudWeatherMapHighTex.SampleLevel(samplerCloudWeatherMapHighTex, uv, 0);
}

/// Mip level at which a volume should be read, given the step size between samples. Point-sampling
/// at steps wider than the noise's features makes density depend on step size (which varies with
/// view angle), causing cloud to pop in as the camera moves; reading a mip as wide as the step
/// averages what would otherwise be skipped, keeping the result stable under camera motion.
float cloudLod(float stepSize, float scale, float resolution)
{
	// A volume tiles every 1/scale world units across `resolution` texels, so one texel spans
	// 1/(scale*resolution) world units.
	return max(0, log2(max(1, stepSize * scale * resolution)));
}

// ---------------------------------------------------------------------------------------------
// The high band: cirrus, cirrocumulus, cirrostratus
// ---------------------------------------------------------------------------------------------

/// 0 removes the layer outright, so its cost and contribution can both be priced against one build.
float cloudHighStrength;

/// Where the ice layer sits in the shell and how thick it is, as fractions of it. Defaults place it
/// at and above the high band's real-world altitude (6000 m), not merely "near the top".
float cloudHighBase;
float cloudHighThickness;

float cloudHighScale;
float cloudHighDensityMultiplier;
float cloudHighCoverageMultiplier;

/// How far the sample coordinate is stretched along the wind for the fibrous form - the streaks are
/// what read as cirrus rather than thin cumulus.
float cloudHighStretch;

/// How much the granular form leans on the volume's cellular channels rather than its Perlin-Worley
/// one, since cirrocumulus is a field of small cells that Worley FBM already describes.
float cloudHighGranularity;

float3 cloudHighWindDir;
float3 cloudHighWind;

/// Density of the ice layer. Deliberately not the low layer's model with different constants: the
/// sample is stretched along the wind into fibres, there is no detail erosion (which would carve
/// cauliflower edges cirrus doesn't have), and density is far lower since cirrus is optically thin.
/// Returns 0 outside its own altitude range before touching a volume.
float sampleCloudHigh(float3 pos, float shellHeight, float stepSize)
{
	if (cloudHighStrength <= 0) { return 0; }

	float h = (shellHeight - cloudHighBase) / max(1e-4, cloudHighThickness);
	if (h <= 0 || h >= 1) { return 0; }

	float4 weather = sampleCloudWeatherHigh(pos);

	float coverage = saturate(weather.r * cloudHighCoverageMultiplier);
	if (coverage <= 0) { return 0; }

	float form = weather.g;

	// Tent weights across fibrous -> granular -> veil, as the low layer's form axis uses.
	float wFibrous = saturate(1 - form * 2);
	float wGranular = saturate(1 - abs(form - 0.5) * 2);
	float wVeil = saturate(form * 2 - 1);

	// Thin, with soft shoulders; smoothstep to avoid the crease a linear ramp leaves at its plateau.
	float profile = smoothstep(0.0, 0.35, h) * (1 - smoothstep(0.55, 1.0, h));

	// Fibrous stretches hard, granular not at all (round cells), veil a little (combed, not flat).
	float stretch = wFibrous * cloudHighStretch
					+ wGranular * 1.0
					+ wVeil * (1 + (cloudHighStretch - 1) * 0.25);

	// Granular is small-scale; the veil is large-scale and nearly continuous.
	float scale = cloudHighScale * (wFibrous * 1.0 + wGranular * 2.6 + wVeil * 0.55);

	// Anisotropy in the sphere's own space: scaling the along-wind component down elongates features along it.
	float along = dot(pos, cloudHighWindDir);
	float3 q = (pos - cloudHighWindDir * along) + cloudHighWindDir * (along / max(1e-3, stretch));

	float4 shape = CloudShapeNoise.SampleLevel(
		samplerCloudShapeNoise, q * scale + cloudHighWind,
		cloudLod(stepSize, scale, cloudShapeResolution));

	float worleyFbm = shape.g * 0.625 + shape.b * 0.25 + shape.a * 0.125;

	// The cellular channels for the granular form, the Perlin-Worley one for the other two.
	float base = lerp(shape.r, worleyFbm, wGranular * cloudHighGranularity);

	// The veil wants near-total cover - same noise, thresholded low enough to become a sheet rather than streaks.
	float effectiveCoverage = saturate(coverage * (1 + wVeil * 0.9));

	float density = cloudRemap(base, 1.0 - effectiveCoverage, 1.0, 0.0, 1.0) * effectiveCoverage;
	if (density <= 0) { return 0; }

	return density * profile * cloudHighDensityMultiplier * cloudHighStrength;
}

/// Density of the low/mid layer - the droplet clouds, seven of the ten genera. Takes `shellHeight`
/// rather than deriving it, so sampleCloudDensity can compute it once for both layers. `cheap` skips
/// detail erosion (the expensive half), used by the light march where the extra fidelity is invisible.
float sampleCloudLow(float3 pos, float shellHeight, bool cheap, float stepSize)
{
	// Out of the layer's reach, bounded from uniforms alone (a conservative bound on the true,
	// weather-dependent floor) so this can reject before paying for the weather tap - worthwhile
	// since the light march calls this six times per view step, per light source.
	float bandCeiling = saturate(cloudWeatherOverrideOn > 0 ? cloudGenusBaseOffset : cloudBaseVariation)
		+ max(0.05, cloudBandThickness);
	if (shellHeight >= bandCeiling) { return 0; }

	float4 weather = sampleCloudWeather(pos);
	float coverage = saturate(weather.r * cloudCoverageMultiplier);
	if (coverage <= 0) { return 0; }

	float precipitation = weather.g;

	// Rain correlates with towering cloud rather than sitting independently of its shape, so a
	// storm region reads as a storm rather than as rain scattered over ordinary cumulus.
	float type = saturate(weather.b + cloudTypeBias + precipitation * cloudPrecipTypePush);

	// The cloud occupies a sub-band of the shell (a field), not the whole shell (which stays global
	// since the ray intersects it directly). Below the base, h = 0 and every gradient is zero there.
	float baseSource = cloudWeatherOverrideOn > 0 ? cloudGenusBaseOffset : weather.a * cloudBaseVariation;
	float baseOffset = saturate(baseSource - precipitation * cloudPrecipBaseDrop);

	// Clamped so the band can't run off the shell top once its floor is raised into the mid
	// altitudes (Altostratus, Altocumulus, Nimbostratus); a no-op at the low band's own settings.
	float thickness = min(max(0.05, cloudBandThickness), 1.0 - baseOffset);
	float h = saturate((shellHeight - baseOffset) / max(0.05, thickness));

	float gradient = cloudHeightGradient(h, type);
	if (gradient <= 0) { return 0; }

	float4 shape = CloudShapeNoise.SampleLevel(
		samplerCloudShapeNoise, pos * cloudShapeScale + cloudShapeWind,
		cloudLod(stepSize, cloudShapeScale, cloudShapeResolution));

	// Worley FBM from G/B/A, weights halving with frequency.
	float worleyFbm = shape.g * 0.625 + shape.b * 0.25 + shape.a * 0.125;

	// R is already Perlin-Worley; pulling its floor toward the Worley FBM gives rounded lumps instead
	// of smooth blobs. The height gradient is split between density and coverage per cloudEdgeSoftness.
	float densityGradient = lerp(gradient, 1.0, cloudEdgeSoftness);
	float coverageGradient = lerp(1.0, gradient, cloudEdgeSoftness);
	float effectiveCoverage = saturate(coverage * coverageGradient);

	float base = saturate(cloudRemap(shape.r, worleyFbm - 1.0, 1.0, 0.0, 1.0)) * densityGradient;

	// Coverage carves the field rather than scaling it: cloud grows outward from where it already is.
	float density = cloudRemap(base, 1.0 - effectiveCoverage, 1.0, 0.0, 1.0) * effectiveCoverage;
	if (density <= 0) { return 0; }

	// Applied to both cheap and full paths so the light march and shadow pass see the same cloud the view ray does.
	density *= 1 + precipitation * cloudPrecipDensity;

	if (cheap) { return density * cloudDensityMultiplier; }

	float3 detail = CloudDetailNoise.SampleLevel(
		samplerCloudDetailNoise, pos * cloudDetailScale + cloudDetailWind,
		cloudLod(stepSize, cloudDetailScale, cloudDetailResolution)).rgb;
	float detailFbm = detail.r * 0.625 + detail.g * 0.25 + detail.b * 0.125;

	// Erosion inverts with height (wispy at the base, billowy at the top), reading as a cauliflower
	// top over a ragged underside rather than uniform fluff.
	float erosion = lerp(1 - detailFbm, detailFbm, saturate(h * 5));
	density = cloudRemap(density, erosion * cloudDetailWeight, 1.0, 0.0, 1.0);

	return max(0, density) * cloudDensityMultiplier;
}

/// Cloud density at a world position: both layers, combined with max() rather than summed, since they
/// occupy different altitudes and occlude rather than accumulate (a sum would make cirrus over
/// cumulus denser than either). The shell-height test is done once here and handed to both layers.
float sampleCloudDensity(float3 pos, bool cheap, float stepSize)
{
	float shellHeight = cloudHeightFraction(pos);
	if (shellHeight <= 0 || shellHeight >= 1) { return 0; }

	float low = sampleCloudLow(pos, shellHeight, cheap, stepSize);
	float high = sampleCloudHigh(pos, shellHeight, stepSize);

	return max(low, high);
}

#endif
