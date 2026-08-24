// The baseline cloud shading model: a textured sheet on a sphere, relief-shaded - the cheap
// counterpart to the volumetric raymarch (one sphere intersection and two texture taps, against up
// to 192 density evaluations per ray). That cost ratio is RQ2's measurement, so it only means
// something if everything else is held constant: hence the shared composite, cost modes and output
// convention, and this file being shared by both delivery paths (post-process and drawn mesh) so
// they cannot disagree about what a cloud looks like.
//
// Deliberately missing, as RQ1 findings about the method rather than implementation shortcomings:
// view-dependent scattering (no phase function, no silver lining), self-shadowing, and silhouette
// (a height field cannot express a cumulonimbus tower as a sheet).
//
// Does not sample the atmosphere's transmittance or sky-view LUTs, for the same reason
// DrawSkyBaseline.shader refuses to: light colour comes from an authored Gradient keyed on sun
// elevation, evaluated once per frame on the CPU.

#ifndef BASELINE_CLOUD_COMMON_INCLUDED
#define BASELINE_CLOUD_COMMON_INCLUDED

// Assets-absolute, never relative: a nested relative include resolves against the including file's
// own directory, not this one.
#include "Assets/Scripts/Shader Common/GeoMath.hlsl"

// ---------------------------------------------------------------------------------------------
// Lighting - global, because both layers are lit by the same sky
// ---------------------------------------------------------------------------------------------

float3 baselineCloudSunDir;

/// Sun and sky colour, evaluated on the CPU from a Gradient keyed on ObserverGeometry's
/// SunElevation01 and bound as flat colours - free at runtime since sun elevation doesn't vary
/// across the screen, and itself a data point for RQ3's authoring comparison.
float3 baselineCloudSunColour;
float3 baselineCloudAmbientColour;

float baselineCloudSunIntensity;
float baselineCloudAmbientIntensity;

/// Wraps the terminator around the back of the relief so the unlit side is shaded rather than
/// black - a bare saturate(N.L) on a sheet reads as embossed metal.
float baselineCloudWrap;

/// How much sunlight reaches the underside. A sheet shaded only by its outward normal looks
/// identical from below, so this is a flat term (light through the layer is dimmer and unshaped by
/// the relief) rather than another N.L.
float baselineCloudBaseLight;

// ---------------------------------------------------------------------------------------------
// Per layer
// ---------------------------------------------------------------------------------------------

/// One cloud layer's geometry and authored look. Passed by value so a second layer is a second call
/// rather than a second copy of this code.
struct BaselineLayer
{
	/// Sphere the sheet sits on, in world units from the planet centre.
	float radius;

	/// How far the height channel can lift the sheet above that sphere - produces parallax within a
	/// layer, since its features then slide against each other as the camera moves.
	float thickness;

	float opacity;
	float contrast;
	float reliefStrength;

	/// Texels across, for the mip footprint below.
	float texelsAcross;

	/// Rotation from world space into the layer's own texture space. A rotation rather than a UV
	/// scroll, since scrolling would tear the pattern at the poles; rotating the sample point keeps
	/// it seamless everywhere.
	float4x4 drift;
};

// A is the lower deck, B the upper. Declared here so the shading path, shadow pass and mesh
// delivery all read the same bindings, so a shadow can never disagree with the cloud casting it.
Texture2D<float4> BaselineCloudLayerA;
SamplerState samplerBaselineCloudLayerA;

Texture2D<float4> BaselineCloudLayerB;
SamplerState samplerBaselineCloudLayerB;

float baselineLayerRadiusA;
float baselineLayerThicknessA;
float baselineLayerOpacityA;
float baselineLayerContrastA;
float baselineLayerReliefA;
float baselineLayerTexelsA;
float4x4 baselineLayerDriftA;

float baselineLayerRadiusB;
float baselineLayerThicknessB;
float baselineLayerOpacityB;
float baselineLayerContrastB;
float baselineLayerReliefB;
float baselineLayerTexelsB;
float4x4 baselineLayerDriftB;

/// 1 or 2. A uniform, so the second deck costs exactly nothing when off - a clean measurement of
/// what the depth cue is worth.
int baselineLayerCount;

/// Sea level. Needed by the shadow pass, which starts its ray at the ground.
float baselineBodyRadius;

BaselineLayer baselineLayerA()
{
	BaselineLayer layer;
	layer.radius = baselineLayerRadiusA;
	layer.thickness = baselineLayerThicknessA;
	layer.opacity = baselineLayerOpacityA;
	layer.contrast = baselineLayerContrastA;
	layer.reliefStrength = baselineLayerReliefA;
	layer.texelsAcross = baselineLayerTexelsA;
	layer.drift = baselineLayerDriftA;
	return layer;
}

BaselineLayer baselineLayerB()
{
	BaselineLayer layer;
	layer.radius = baselineLayerRadiusB;
	layer.thickness = baselineLayerThicknessB;
	layer.opacity = baselineLayerOpacityB;
	layer.contrast = baselineLayerContrastB;
	layer.reliefStrength = baselineLayerReliefB;
	layer.texelsAcross = baselineLayerTexelsB;
	layer.drift = baselineLayerDriftB;
	return layer;
}

/// Local copy of the ray-sphere test, not an include of CloudCommon.hlsl - that would drag the
/// entire volumetric density model into a shader whose whole claim is that it doesn't have one.
///
/// Returns (distance to the sphere, distance through it). x is 0 when already inside; y is 0 on a miss.
float2 baselineRaySphere(float radius, float3 rayOrigin, float3 rayDir)
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

/// Where this ray meets the layer's sphere; negative if it never does. From above this is the near
/// hit (sheet top); from below, the near hit clamps to zero so the exit (underside) is the answer -
/// both fall out of the same expression with no camera-position cases needed.
float baselineLayerHit(float radius, float3 rayOrigin, float3 rayDir)
{
	float2 hit = baselineRaySphere(radius, rayOrigin, rayDir);
	if (hit.y <= 0) { return -1; }
	return hit.x > 0 ? hit.x : hit.x + hit.y;
}

/// Angular size of one screen pixel, in radians. Bound per frame; used for the mip footprint.
float baselineCloudPixelAngle;

/// Mip level at which the layer should be read here, following the same rule the density volumes
/// and the cloud shadow map do: read at the scale the consumer can represent, or point-sampling
/// aliases into a crawling shimmer. Divided by the incidence cosine, since a grazing view stretches
/// a pixel's footprint along the surface without changing its angular size.
float baselineLodForFootprint(BaselineLayer layer, float footprint)
{
	float texelWorld = layer.radius * 6.28318530718 / max(1.0, layer.texelsAcross);
	return max(0, log2(max(1e-6, footprint / texelWorld)));
}

float baselineLayerLod(BaselineLayer layer, float distance, float cosIncidence)
{
	return baselineLodForFootprint(layer, distance * baselineCloudPixelAngle / max(0.05, cosIncidence));
}

/// How much this deck hides, at a point on it. Shared by the shading path and the shadow pass so the
/// two cannot disagree about where a cloud is. `cosIncidence` (between the ray and the outward
/// normal `up`) accounts for a grazing ray crossing more cloud than a perpendicular one.
float baselineLayerCoverage(
	Texture2D<float4> layerTex, SamplerState layerSampler, BaselineLayer layer,
	float3 up, float cosIncidence, float lod, out float4 texel)
{
	float3 pTex = mul((float3x3)layer.drift, up);
	texel = layerTex.SampleLevel(layerSampler, pointToUV(pTex), lod);

	float coverage = saturate(pow(saturate(texel.a), layer.contrast) * layer.opacity);

	// Beer through a slab. Textbook (Real-Time Rendering, 4th ed.), which keeps the baseline
	// citeable as a named technique rather than an ad hoc approximation.
	return 1 - pow(max(1e-4, 1 - coverage), 1.0 / max(0.08, cosIncidence));
}

/// The layer's contribution along a ray, in the same convention the volumetric march returns: rgb is
/// premultiplied added radiance, a is how much behind it survives - so the shared composite can
/// blend either renderer with the same expression. `maxDistance` is scene depth, since terrain rises
/// above the layer's sphere. `hitDistance` lets the caller order two layers front-to-back (which is
/// nearer isn't fixed - it flips as the camera crosses the deck); a miss returns a huge distance so
/// it sorts last without a special case.
float4 baselineCloudLayer(
	Texture2D<float4> layerTex, SamplerState layerSampler, BaselineLayer layer,
	float3 rayOrigin, float3 rayDir, float maxDistance, out float hitDistance)
{
	hitDistance = 1e20;

	float t = baselineLayerHit(layer.radius, rayOrigin, rayDir);
	if (t < 0 || t >= maxDistance) { return float4(0, 0, 0, 1); }

	float3 up = normalize(rayOrigin + rayDir * t);
	float3x3 drift = (float3x3)layer.drift;

	float cosIncidence = abs(dot(rayDir, up));

	float4 s;
	float alpha = baselineLayerCoverage(
		layerTex, layerSampler, layer, up, cosIncidence, baselineLayerLod(layer, t, cosIncidence), s);

	// One parallax refinement: re-intersect at the radius the height channel says this cloud actually
	// reaches, so the sample lands on the cloud top rather than the sheet's nominal sphere. A single
	// step, not a search - the height field is smooth at screen-pixel scale.
	if (layer.thickness > 1e-4)
	{
		float lifted = baselineLayerHit(layer.radius + s.b * layer.thickness, rayOrigin, rayDir);
		if (lifted >= 0 && lifted < maxDistance)
		{
			t = lifted;
			up = normalize(rayOrigin + rayDir * t);
			cosIncidence = abs(dot(rayDir, up));
			alpha = baselineLayerCoverage(
				layerTex, layerSampler, layer, up, cosIncidence,
				baselineLayerLod(layer, t, cosIncidence), s);
		}
	}

	if (alpha <= 0.001) { return float4(0, 0, 0, 1); }

	// Only now, once there's actually cloud here: a hole in the top layer must not occlude the bottom one.
	hitDistance = t;

	// Everything below happens in texture space - the drift rotation is applied going in and undone
	// on the normal coming out, so relief stays attached to the clouds as they move.
	float3 pTex = mul(drift, up);

	// Tangent-space normal, unpacked and re-steepened by scaling xy and renormalising (the stored
	// normal is already unit length, so scaling it directly would only change length, not slope).
	float2 nxy = s.rg * 2 - 1;
	float nz = sqrt(saturate(1 - dot(nxy, nxy)));
	float3 nTangent = normalize(float3(nxy * layer.reliefStrength, max(1e-3, nz)));

	// The bake's own frame - has to match, or the relief lights from the wrong side.
	float3 poleAxis = float3(0, 1, 0);
	float3 east = abs(pTex.y) > 0.9999 ? float3(1, 0, 0) : normalize(cross(poleAxis, pTex));
	float3 north = cross(pTex, east);
	float3 nTex = east * nTangent.x + north * nTangent.y + pTex * nTangent.z;

	// Back out of texture space: mul(v, M) is M-transpose times v, the inverse of a rotation.
	float3 worldNormal = mul(nTex, drift);

	float ndl = dot(worldNormal, baselineCloudSunDir);
	float lit = saturate((ndl + baselineCloudWrap) / (1 + baselineCloudWrap));

	float3 topColour =
		baselineCloudSunColour * lit * baselineCloudSunIntensity +
		baselineCloudAmbientColour * baselineCloudAmbientIntensity;

	// The underside: ambient plus whatever came through the sheet, flat rather than relief-shaded.
	float3 baseColour =
		baselineCloudAmbientColour * baselineCloudAmbientIntensity +
		baselineCloudSunColour * baselineCloudSunIntensity *
			saturate(dot(up, baselineCloudSunDir)) * baselineCloudBaseLight;

	// 1 looking down on the sheet, 0 looking up at it from below.
	float viewingTop = saturate(dot(up, -rayDir));
	float3 colour = lerp(baseColour, topColour, viewingTop);

	return float4(colour * alpha, 1 - alpha);
}

/// Two layers composited in view order, sorted per pixel since which layer is nearer flips as the
/// camera crosses the deck - both orders can appear in the same frame near the horizon. This
/// per-radius sorting is where the depth cue comes from: the two sheets slide across one another as
/// the camera moves, which a single flat overlay cannot do.
float4 baselineCombineLayers(float4 a, float aDistance, float4 b, float bDistance)
{
	bool aFirst = aDistance <= bDistance;
	float4 near = aFirst ? a : b;
	float4 far = aFirst ? b : a;

	// Far under near, both premultiplied: the far layer's contribution is dimmed by what the near one hides.
	return float4(near.rgb + far.rgb * near.a, near.a * far.a);
}

#endif
