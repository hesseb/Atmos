// The baseline cloud shading model: a textured sheet on a sphere, relief-shaded.
//
// This is the cheap counterpart to the volumetric raymarch. Where CloudCommon.hlsl evaluates a
// density field at up to 192 points along a ray and marches toward the sun from each of them, this
// intersects one sphere and takes two texture taps. That ratio is the measurement RQ2 asks for, and
// it only means anything if everything around it is held constant - hence the shared composite, the
// shared cost modes and the shared output convention.
//
// Shared by BOTH delivery paths - the post-process and the drawn mesh - so the two cannot disagree
// about what a cloud looks like, the same way ObserverGeometry is shared by the baseline sky and
// the baseline aerial perspective "so the two cannot disagree about what time of day it is". A
// visible difference between the deliveries would then be a real finding about the delivery rather
// than two shading models drifting apart.
//
// What this deliberately does NOT do, recorded rather than hidden because each is an RQ1 finding
// about the method rather than a shortcoming of the implementation:
//
//   - No view-dependent scattering. No phase function, so no forward brightening and no silver
//     lining: the baseline cannot respond to being looked at toward the sun at all.
//   - No self-shadowing. Relief shading fakes the surface, but one cloud cannot shade another and
//     no cloud has an inside.
//   - No silhouette. A height field can suggest thickness, but a cumulonimbus tower against the
//     sky is not expressible as a sheet - which is the central RQ1 finding.
//
// It does not sample the atmosphere's transmittance LUT or sky-view LUT. DrawSkyBaseline.shader
// refuses the same lookup because it "would smuggle physically based data into the baseline", and
// the reasoning carries over exactly: light colour here comes from an authored Gradient keyed on
// sun elevation, evaluated once per frame on the CPU.

#ifndef BASELINE_CLOUD_COMMON_INCLUDED
#define BASELINE_CLOUD_COMMON_INCLUDED

// Assets-absolute, never relative: a nested relative include resolves against the *including*
// file's directory, which is what turned the ocean pink the first time.
#include "Assets/Scripts/Shader Common/GeoMath.hlsl"

// ---------------------------------------------------------------------------------------------
// Lighting - global, because both layers are lit by the same sky
// ---------------------------------------------------------------------------------------------

float3 baselineCloudSunDir;

/// Sun and sky colour, both evaluated on the CPU from a Gradient keyed on ObserverGeometry's
/// SunElevation01 and bound as flat colours.
///
/// A Gradient is about the most artist-facing authoring surface Unity has, which is itself a data
/// point for RQ3's authoring comparison - and it costs nothing at run time, because sun elevation
/// does not vary across the screen. AerialPerspectiveSimple drives its haze colour the same way and
/// for the same reasons.
float3 baselineCloudSunColour;
float3 baselineCloudAmbientColour;

float baselineCloudSunIntensity;
float baselineCloudAmbientIntensity;

/// Wraps the terminator around the back of the relief so the unlit side is shaded rather than
/// black. Real cloud is deep enough to scatter light well past the geometric terminator, and a
/// bare saturate(N.L) on a sheet reads as embossed metal.
float baselineCloudWrap;

/// How much sunlight reaches the UNDERSIDE. A sheet shaded only by its outward normal looks
/// identical from below, which is wrong in the one view a strategy camera never takes but a
/// low-flying one always does. Light that has come through the layer is dimmer and unshaped by the
/// relief, so this is a flat term rather than another N.L.
float baselineCloudBaseLight;

// ---------------------------------------------------------------------------------------------
// Per layer
// ---------------------------------------------------------------------------------------------

/// One cloud layer's geometry and authored look. Passed by value so stage 4's second layer is a
/// second call rather than a second copy of this code.
struct BaselineLayer
{
	/// Sphere the sheet sits on, in world units from the planet centre.
	float radius;

	/// How far the height channel can lift the sheet above that sphere. This is what produces
	/// parallax WITHIN a layer - the sheet is not flat, so its features slide against each other as
	/// the camera moves, which a single flat overlay cannot do.
	float thickness;

	float opacity;
	float contrast;
	float reliefStrength;

	/// Texels across, for the mip footprint below.
	float texelsAcross;

	/// Rotation from world space into the layer's own texture space. A ROTATION rather than a UV
	/// scroll: scrolling u slides features along latitude lines and tears them at the poles, which
	/// is the mistake the volumetric's weather map already documents. Rotating the sample point
	/// moves the whole pattern rigidly, so it is seamless everywhere including over a pole.
	float4x4 drift;
};

/// Local copy of the ray-sphere test rather than an include of CloudCommon.hlsl.
///
/// Including that header would drag the entire volumetric density model - three texture bindings
/// and twenty uniforms - into a shader whose whole claim is that it does not have one. The baseline
/// has to be independently costed, so it gets its own twelve lines.
///
/// Returns (distance to the sphere, distance through it). x is 0 when already inside; y is 0 on a
/// miss.
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

/// Where this ray meets the layer's sphere. Returns a negative distance if it never does.
///
/// From above, that is the near hit - the top of the sheet. From below, the camera is inside the
/// sphere and the near hit is clamped to zero, so the exit is the answer: the underside. Both fall
/// out of the same expression, which is why there are no camera-position cases here.
float baselineLayerHit(float radius, float3 rayOrigin, float3 rayDir)
{
	float2 hit = baselineRaySphere(radius, rayOrigin, rayDir);
	if (hit.y <= 0) { return -1; }
	return hit.x > 0 ? hit.x : hit.x + hit.y;
}

/// Angular size of one screen pixel, in radians. Bound per frame; used for the mip footprint.
float baselineCloudPixelAngle;

/// Mip level at which the layer should be read here.
///
/// The same rule the density volumes and the cloud shadow map both follow: read the texture at the
/// scale the consumer can actually represent. A sheet 2048 texels around, seen from orbit, puts
/// dozens of texels inside one pixel, and point-sampling that aliases into a crawling shimmer as
/// the camera moves - the exact failure that made the cloud shadows flicker.
///
/// Divided by the incidence cosine, because a grazing view stretches a pixel's footprint along the
/// surface without changing its angular size. Without that, the horizon aliases while everything
/// else is clean.
float baselineLayerLod(BaselineLayer layer, float distance, float cosIncidence)
{
	float footprint = distance * baselineCloudPixelAngle / max(0.05, cosIncidence);
	float texelWorld = layer.radius * 6.28318530718 / max(1.0, layer.texelsAcross);
	return max(0, log2(max(1e-6, footprint / texelWorld)));
}

/// The layer's contribution along a ray.
///
/// Returns the same convention the volumetric march does - rgb is the radiance the layer ADDS,
/// premultiplied, and a is the fraction of whatever is behind it that survives - so the shared
/// composite blends both renderers with the same expression and the two can be swapped without the
/// compositing changing.
///
/// `maxDistance` is the scene depth. Terrain rises above the sphere the layer sits on, so depth -
/// not a ground intersection - is what actually occludes cloud, exactly as in the volumetric.
///
/// `hitDistance` comes back so the caller can order two layers front-to-back. Which one is nearer
/// is not fixed: from above the deck the upper layer is in front, from below it is behind, and the
/// camera crosses between them. Returns a huge distance on a miss so a missing layer sorts last
/// without a special case.
float4 baselineCloudLayer(
	Texture2D<float4> layerTex, SamplerState layerSampler, BaselineLayer layer,
	float3 rayOrigin, float3 rayDir, float maxDistance, out float hitDistance)
{
	hitDistance = 1e20;

	float t = baselineLayerHit(layer.radius, rayOrigin, rayDir);
	if (t < 0 || t >= maxDistance) { return float4(0, 0, 0, 1); }

	float3 up = normalize(rayOrigin + rayDir * t);
	float3x3 drift = (float3x3)layer.drift;

	// Everything below happens in TEXTURE space - the sphere as the bake saw it. The drift rotation
	// is applied to the sample point going in and undone on the normal coming out, which keeps the
	// relief attached to the clouds as they move rather than lighting a stationary shell.
	float3 pTex = mul(drift, up);

	float cosIncidence = abs(dot(rayDir, up));
	float lod = baselineLayerLod(layer, t, cosIncidence);

	float4 s = layerTex.SampleLevel(layerSampler, pointToUV(pTex), lod);

	// One parallax refinement. The height channel says how far above the base sphere this bit of
	// cloud actually reaches, so re-intersecting at that radius puts the sample where the cloud top
	// is rather than where the sheet nominally sits. At a strategy camera's tilt that is the
	// difference between a decal and something with a top and a side.
	//
	// A single step, not a search: the height field is smooth at the scale one screen pixel covers,
	// and a second tap is already half the shading cost of this entire renderer.
	if (layer.thickness > 1e-4)
	{
		float lifted = baselineLayerHit(layer.radius + s.b * layer.thickness, rayOrigin, rayDir);
		if (lifted >= 0 && lifted < maxDistance)
		{
			t = lifted;
			up = normalize(rayOrigin + rayDir * t);
			pTex = mul(drift, up);
			cosIncidence = abs(dot(rayDir, up));
			s = layerTex.SampleLevel(layerSampler, pointToUV(pTex), baselineLayerLod(layer, t, cosIncidence));
		}
	}

	float coverage = saturate(pow(saturate(s.a), layer.contrast) * layer.opacity);
	if (coverage <= 0.001) { return float4(0, 0, 0, 1); }

	// Only now, once there is actually cloud here: a layer whose texture is clear at this point
	// must not sort in front of one that is not, or a hole in the top layer would still occlude the
	// bottom one.
	hitDistance = t;

	// Beer through a slab: a grazing ray crosses more cloud than a perpendicular one, so the same
	// sheet is more opaque toward the horizon. One line, and it is most of what stops a flat layer
	// reading as flat - without it the deck simply stops at the horizon instead of thickening into
	// it. Textbook (Real-Time Rendering, 4th ed.), which keeps the baseline citeable.
	float alpha = 1 - pow(max(1e-4, 1 - coverage), 1.0 / max(0.08, cosIncidence));

	// Tangent-space normal, unpacked and re-steepened. Scaling xy and renormalising rather than
	// scaling the unpacked vector: the stored normal is already unit length, so multiplying it
	// outright would only change its length and not its slope.
	float2 nxy = s.rg * 2 - 1;
	float nz = sqrt(saturate(1 - dot(nxy, nxy)));
	float3 nTangent = normalize(float3(nxy * layer.reliefStrength, max(1e-3, nz)));

	// The bake's own frame: east from the pole axis, north completing it. It has to match, or the
	// relief lights from the wrong side.
	float3 poleAxis = float3(0, 1, 0);
	float3 east = abs(pTex.y) > 0.9999 ? float3(1, 0, 0) : normalize(cross(poleAxis, pTex));
	float3 north = cross(pTex, east);
	float3 nTex = east * nTangent.x + north * nTangent.y + pTex * nTangent.z;

	// Back out of texture space. mul(v, M) is M-transpose times v, which for a rotation is its
	// inverse - so this undoes the drift exactly rather than approximately.
	float3 worldNormal = mul(nTex, drift);

	float ndl = dot(worldNormal, baselineCloudSunDir);
	float lit = saturate((ndl + baselineCloudWrap) / (1 + baselineCloudWrap));

	float3 topColour =
		baselineCloudSunColour * lit * baselineCloudSunIntensity +
		baselineCloudAmbientColour * baselineCloudAmbientIntensity;

	// The underside: ambient, plus whatever came through the sheet. Flat rather than relief-shaded,
	// because light that has scattered through a cloud has lost the surface it came in on.
	float3 baseColour =
		baselineCloudAmbientColour * baselineCloudAmbientIntensity +
		baselineCloudSunColour * baselineCloudSunIntensity *
			saturate(dot(up, baselineCloudSunDir)) * baselineCloudBaseLight;

	// 1 looking down on the sheet, 0 looking up at it from below.
	float viewingTop = saturate(dot(up, -rayDir));
	float3 colour = lerp(baseColour, topColour, viewingTop);

	return float4(colour * alpha, 1 - alpha);
}

/// Two layers composited in view order.
///
/// Sorted per pixel rather than assumed, because which layer is in front is not a constant: from
/// above the deck the upper one is nearer, from below it is behind, and the camera crosses between
/// them. Assuming an order puts the high cirrus in front of the cumulus it should be behind for
/// every pixel below the deck - and near the horizon both orders appear in the same frame.
///
/// This is where the depth cue comes from. Two sheets at different radii subtend different angles
/// from a moving camera, so they slide across one another exactly as real cloud decks do. A single
/// flat overlay cannot do it on a globe at all, which is why the plan spends a second texture tap
/// on it rather than making the one layer denser.
float4 baselineCombineLayers(float4 a, float aDistance, float4 b, float bDistance)
{
	bool aFirst = aDistance <= bDistance;
	float4 near = aFirst ? a : b;
	float4 far = aFirst ? b : a;

	// Compositing far under near, both premultiplied: the background survives both, and the far
	// layer's own contribution is dimmed by whatever the near one hides.
	return float4(near.rgb + far.rgb * near.a, near.a * far.a);
}

#endif
