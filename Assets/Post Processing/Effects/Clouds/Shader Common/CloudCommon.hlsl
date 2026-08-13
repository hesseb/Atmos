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

/// The ice layer's weather: R coverage, G form. Its own field because high cloud arrives on its own
/// weather - a cirrus shield runs ahead of a front and has nothing to do with the cumulus below it.
Texture2D<float4> CloudWeatherMapHighTex;
SamplerState samplerCloudWeatherMapHighTex;

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

/// The spans of this ray that lie inside the cloud shell - there can be TWO of them.
///
/// A ray leaving the shell downward does not stay gone. The planet curves away beneath it, so it
/// sinks below the cloud base, reaches a lowest point, and climbs back through the base further
/// along. Looking toward the horizon from in or under the deck, most of what you see is that
/// second span: the underside of the cloud stretching away past the dip.
///
/// Returning only the first span drops all of it, and leaves a hard seam across the view with
/// cloud above and nothing below, all the way to the horizon.
///
/// Expressed as the outer sphere's chord minus the inner sphere's, which needs no cases for where
/// the camera is: above the shell, inside it and below it all fall out of the same subtraction.
/// An empty span comes back with end <= start and is skipped by the caller.
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

/// How thick the cloud band is, as a fraction of the shell.
///
/// The band TRANSLATES up and down; it does not stretch to meet the shell ceiling. Mapping
/// [baseOffset, 1] onto [0, 1] instead - which is what this did at first - compresses the band as
/// the base rises, so the top stays pinned to the shell however much the base moves and every cloud
/// reaches exactly the same height. That is the flattened-off look: not clouds that happen to agree,
/// clouds that were made to.
float cloudBandThickness;

/// How much of the height gradient acts on COVERAGE rather than on density.
///
/// 0 is Schneider's arrangement, where the gradient multiplies the shape noise and the coverage
/// threshold is applied afterwards. That has a sharp consequence: the result is nonzero only where
/// shapeNoise > (1 - coverage) / gradient, and shape noise cannot exceed 1, so the whole layer
/// vanishes the instant the gradient drops below 1 - coverage. At coverage 0.5 that is halfway down
/// the ramp, at the same height everywhere - the layer does not fade out, it stops, in a flat plane.
///
/// Moving the gradient onto coverage instead lowers the threshold progressively, so the highest
/// noise peaks survive longest and the edge breaks up into thinning, scattered cloud rather than
/// ending at a surface.
float cloudEdgeSoftness;

/// What precipitation does. Rain-bearing cloud is thicker, taller and hangs lower, so one field
/// drives all three rather than three unrelated ones scattering across the globe.
float cloudPrecipDensity;
float cloudPrecipTypePush;
float cloudPrecipBaseDrop;

/// Texels per axis in each volume, so a march step can be converted into a mip level.
float cloudShapeResolution;
float cloudDetailResolution;

float3 cloudShapeWind;
float3 cloudDetailWind;

/// The vertical profile of the low/mid layer, blended along the FORM axis.
///
/// This is half of the control RQ1 scores. THESIS.md 3.5 makes the cloud-genera table the rubric,
/// and that table is two-dimensional: altitude BAND crossed with form. This function is the form
/// half - sheet, heap, tower - and it works in the layer's OWN normalised height, so where the layer
/// sits is the band half and is decided by the base offset in sampleCloudDensity.
///
/// That factoring is what reaches the mid band. Altostratus, Altocumulus and Nimbostratus are not
/// new shapes; they are these shapes with their floor at 2000-6000 m, which is what the taxonomy
/// says they are. Only the high band needs a genuinely different profile, because ice behaves
/// differently from droplets - see sampleCloudHigh.
float cloudHeightGradient(float h, float type)
{
	// smoothstep rather than a linear remap: a linear ramp meets its plateau at a corner, and that
	// corner is a visible crease along the top and bottom of the layer. Same extents, no crease.
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

/// Pins every weather field to one genus, globally, bypassing the maps.
///
/// Authoring scaffolding rather than a rendering path: it feeds the SAME density model, so what it
/// shows is genuinely what the renderer can produce. RQ1 needs one image of each of the ten genera,
/// and hunting for a region where the noise happens to have produced Cirrocumulus is not a workflow.
///
/// It is also evidence for RQ3 in its own right. The mapping from "I want Altostratus" to a set of
/// parameter values IS the authoring-flexibility axis the report grades, and the table in
/// CloudEffect.GenusSettings is that mapping written down.
float cloudWeatherOverrideOn;
float4 cloudWeatherOverride;      // coverage, precipitation, form, unused
float2 cloudHighWeatherOverride;  // coverage, form

/// The low layer's floor, supplied directly by the override rather than through
/// weather.a * cloudBaseVariation - so a genus lands in its stated altitude band whatever the base
/// variation happens to be tuned to. Precipitation still lowers it, because that is what the
/// -nimbus affix does and Nimbostratus depends on it.
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

// ---------------------------------------------------------------------------------------------
// The high band: cirrus, cirrocumulus, cirrostratus
// ---------------------------------------------------------------------------------------------

/// 0 removes the layer outright, and exactly - so its cost and its contribution can both be priced
/// against one build, the same shape as cloudAerialStrength and the deck radiance map's strength.
float cloudHighStrength;

/// Where the ice layer sits in the shell and how thick it is, as fractions of it. The high band is
/// above 6000 m; at 0.91 km per world unit, with a shell running 0.91 to 9.1 km, that is shell
/// height 0.64 and up - so the defaults put it there rather than merely "near the top".
float cloudHighBase;
float cloudHighThickness;

float cloudHighScale;
float cloudHighDensityMultiplier;
float cloudHighCoverageMultiplier;

/// How far the sample coordinate is stretched along the wind for the fibrous form. This is what
/// makes cirrus read as ice rather than as thin cumulus: the streaks are the visual signature of
/// the genus, and an unstretched sample gives faint puffs instead.
float cloudHighStretch;

/// How much the granular form leans on the volume's cellular channels rather than its Perlin-Worley
/// one. Cirrocumulus is a field of small cells, and the Worley FBM already describes exactly that.
float cloudHighGranularity;

float3 cloudHighWindDir;
float3 cloudHighWind;

/// Density of the ice layer.
///
/// Deliberately NOT the low layer's model with different constants. Three things differ, and each is
/// a property of ice rather than a tuning choice:
///
///   - The sample coordinate is stretched along the wind, so features elongate into fibres.
///   - There is no detail erosion. That is the expensive half of the low layer's density function,
///     and it is also wrong here: it carves billowy cauliflower edges, which is the one thing
///     cirrus does not have.
///   - Density is far lower. Cirrus is optically thin - the sun is visible through it.
///
/// Returns 0 outside its own altitude range before touching a volume, which is what makes a second
/// layer affordable: the two bands do not overlap, so a sample is inside at most one of them.
float sampleCloudHigh(float3 pos, float shellHeight, float stepSize)
{
	if (cloudHighStrength <= 0) { return 0; }

	float h = (shellHeight - cloudHighBase) / max(1e-4, cloudHighThickness);
	if (h <= 0 || h >= 1) { return 0; }

	float4 weather = sampleCloudWeatherHigh(pos);

	float coverage = saturate(weather.r * cloudHighCoverageMultiplier);
	if (coverage <= 0) { return 0; }

	float form = weather.g;

	// Tent weights across fibrous -> granular -> veil, the same construction the low layer's form
	// axis uses, so only two are ever active at once.
	float wFibrous = saturate(1 - form * 2);
	float wGranular = saturate(1 - abs(form - 0.5) * 2);
	float wVeil = saturate(form * 2 - 1);

	// Thin, with soft shoulders. smoothstep rather than a linear ramp for the reason the low layer
	// documents: a linear ramp meets its plateau at a corner, and the corner is a visible crease.
	float profile = smoothstep(0.0, 0.35, h) * (1 - smoothstep(0.55, 1.0, h));

	// Fibrous stretches hard along the wind. Granular does not stretch at all, because cells are
	// round. The veil stretches a little, which is what makes a cirrostratus sheet look combed
	// rather than merely flat.
	float stretch = wFibrous * cloudHighStretch
					+ wGranular * 1.0
					+ wVeil * (1 + (cloudHighStretch - 1) * 0.25);

	// Granular is small-scale; the veil is large-scale and nearly continuous.
	float scale = cloudHighScale * (wFibrous * 1.0 + wGranular * 2.6 + wVeil * 0.55);

	// Anisotropy, in the sphere's own space. Scaling the along-wind component DOWN makes the noise
	// vary more slowly in that direction, which is what elongates a feature along it.
	float along = dot(pos, cloudHighWindDir);
	float3 q = (pos - cloudHighWindDir * along) + cloudHighWindDir * (along / max(1e-3, stretch));

	float4 shape = CloudShapeNoise.SampleLevel(
		samplerCloudShapeNoise, q * scale + cloudHighWind,
		cloudLod(stepSize, scale, cloudShapeResolution));

	float worleyFbm = shape.g * 0.625 + shape.b * 0.25 + shape.a * 0.125;

	// The cellular channels for the granular form, the Perlin-Worley one for the other two.
	float base = lerp(shape.r, worleyFbm, wGranular * cloudHighGranularity);

	// The veil wants near-total cover, which is what separates cirrostratus from cirrus: the same
	// noise, thresholded so much lower that it stops being streaks and becomes a sheet.
	float effectiveCoverage = saturate(coverage * (1 + wVeil * 0.9));

	float density = cloudRemap(base, 1.0 - effectiveCoverage, 1.0, 0.0, 1.0) * effectiveCoverage;
	if (density <= 0) { return 0; }

	return density * profile * cloudHighDensityMultiplier * cloudHighStrength;
}

/// Density of the low/mid layer - the droplet clouds, seven of the ten genera.
///
/// Takes `shellHeight` rather than deriving it, so sampleCloudDensity can compute it once and hand
/// it to both layers. `cheap` skips the detail erosion, which is the expensive half - used by the
/// light march, where the extra fidelity is invisible.
float sampleCloudLow(float3 pos, float shellHeight, bool cheap, float stepSize)
{
	// Out of the layer's reach, decided from UNIFORMS ALONE and therefore before the weather tap.
	//
	// The high layer already rejects on altitude first (see sampleCloudHigh), which is what its
	// "returns zero before touching a volume" claim rests on. This one could not: the band's floor
	// comes out of the weather map's alpha, so the obvious test needs the very tap it would save.
	//
	// It can still be bounded, though. baseOffset is a saturate of either cloudGenusBaseOffset or
	// weather.a * cloudBaseVariation, and weather.a cannot exceed 1 - so whichever branch is live,
	// the floor is capped by a uniform. Precipitation only ever pushes the floor DOWN, so ignoring
	// it keeps the bound conservative. The divisor h is measured against is capped the same way.
	//
	// Every gradient in cloudHeightGradient is zero at h = 1, so a sample above floor + thickness
	// contributes nothing, and this rejects a strict subset of what the gradient test at the bottom
	// already rejects. It is a saving, not a change: no sample that would have returned non-zero is
	// turned away.
	//
	// Worth having because the light march calls this six times per view step, per light source,
	// and the tap it skips is a normalize plus an atan2 plus an asin before the fetch.
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

	// The cloud occupies a SUB-BAND of the shell rather than all of it.
	//
	// The shell itself has to stay global - it is what the ray intersects - but where the cloud sits
	// inside it can be a field. Cloud type already moves the top, because the three height gradients
	// end at different heights, but all of them start at the bottom; without this the base is the
	// one thing identical everywhere on the planet.
	//
	// Below the base this gives h = 0, and every gradient is zero there, so the region under the
	// cloud empties out without a separate test.
	float baseSource = cloudWeatherOverrideOn > 0 ? cloudGenusBaseOffset : weather.a * cloudBaseVariation;
	float baseOffset = saturate(baseSource - precipitation * cloudPrecipBaseDrop);

	// Clamped so the band cannot run off the top of the shell once its floor is raised into the mid
	// altitudes - which is what Altostratus, Altocumulus and Nimbostratus need. A no-op at the low
	// band's settings, where base plus thickness already stays under 1, so the existing look is
	// untouched: this generalises the band rather than retuning it.
	float thickness = min(max(0.05, cloudBandThickness), 1.0 - baseOffset);
	float h = saturate((shellHeight - baseOffset) / max(0.05, thickness));

	float gradient = cloudHeightGradient(h, type);
	if (gradient <= 0) { return 0; }

	float4 shape = CloudShapeNoise.SampleLevel(
		samplerCloudShapeNoise, pos * cloudShapeScale + cloudShapeWind,
		cloudLod(stepSize, cloudShapeScale, cloudShapeResolution));

	// Worley FBM from G/B/A, weights halving with frequency.
	float worleyFbm = shape.g * 0.625 + shape.b * 0.25 + shape.a * 0.125;

	// R is already Perlin-Worley; pulling its floor down to the Worley FBM is what gives the base
	// rounded lumps instead of smooth blobs.
	// The height gradient is split between the two terms - see cloudEdgeSoftness. At 0 all of it
	// multiplies the density, which is Schneider's arrangement and ends the layer in a plane; at 1
	// all of it scales the coverage, which thins the edge into scattered cloud instead.
	float densityGradient = lerp(gradient, 1.0, cloudEdgeSoftness);
	float coverageGradient = lerp(1.0, gradient, cloudEdgeSoftness);
	float effectiveCoverage = saturate(coverage * coverageGradient);

	float base = saturate(cloudRemap(shape.r, worleyFbm - 1.0, 1.0, 0.0, 1.0)) * densityGradient;

	// Coverage carves the field rather than scaling it: more coverage lowers the threshold the base
	// has to clear, so cloud grows outward from where it already is instead of fading up uniformly.
	float density = cloudRemap(base, 1.0 - effectiveCoverage, 1.0, 0.0, 1.0) * effectiveCoverage;
	if (density <= 0) { return 0; }

	// Rain-bearing cloud is thicker. Applied to both the cheap and full paths so the light march
	// and the shadow pass see the same cloud the view ray does.
	density *= 1 + precipitation * cloudPrecipDensity;

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

/// Cloud density at a world position: both layers.
///
/// Two of them, because THESIS.md 3.5's genera table - which is RQ1's rubric - is two-dimensional,
/// altitude band crossed with form, and one layer with one form axis can only walk one row of it.
///
/// max(), not a sum. The two occupy different altitudes and occlude one another rather than
/// accumulating, and max keeps the result bounded so the existing extinction tuning still holds.
/// A sum would also make cirrus over cumulus denser than either, which is backwards - thin ice above
/// a deck does not thicken it.
///
/// The shell-height test is done ONCE here and handed to both, which is also what makes the second
/// layer cheap: each returns zero outside its own altitude range before touching a volume.
///
/// Both now reject before the WEATHER tap as well, which is the more valuable of the two - the
/// volume tap was already behind a coverage test, while the weather tap sat in front of everything
/// and costs an atan2 and an asin on top of the fetch. The high layer tests its own range directly;
/// the low layer bounds its band from uniforms, because its floor is a field. See both.
///
/// The two ranges are not guaranteed disjoint, incidentally - the low band's ceiling is clamped to
/// the top of the shell, which is territory the high band also occupies - so a sample can be inside
/// both. max() below is what makes that harmless rather than additive.
float sampleCloudDensity(float3 pos, bool cheap, float stepSize)
{
	float shellHeight = cloudHeightFraction(pos);
	if (shellHeight <= 0 || shellHeight >= 1) { return 0; }

	float low = sampleCloudLow(pos, shellHeight, cheap, stepSize);
	float high = sampleCloudHigh(pos, shellHeight, stepSize);

	return max(low, high);
}

#endif
