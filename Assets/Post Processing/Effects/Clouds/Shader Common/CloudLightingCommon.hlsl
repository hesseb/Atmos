// The cloud's OPTICAL MODEL: how light is scattered, absorbed and occluded inside the medium.
//
// Split out of Clouds.shader so the view-ray march and the downward-radiance bake share it. The rule
// is the one CloudCommon.hlsl states about density and TransmittanceCommon.hlsl states about its
// parameterisation: two consumers of the same physics must call one definition, because a mismatch
// between them produces a plausible image rather than an obvious fault - clouds whose undersides
// disagree with the light they cast on the sea below, which reads as a compositing error and sends
// you looking in the wrong place.
//
// What is here is everything that depends only on the medium: the phase function, Beer-Powder, and
// the cone-sampled light march.
//
// What is deliberately NOT here is the assembly - how sun, skylight and moonlight are combined into
// a radiance. That is not one integral restated twice, it is two different integrals. The view ray
// asks what reaches the camera along an arbitrary direction and carries a moon term and a silver
// lining; the bake asks what leaves the deck's underside, straight down, where neither applies.
// Sharing the optical model and letting each consumer assemble its own colour is the honest split.
//
// Compute-safe, and it has to be: CloudCommon.hlsl already chose Texture3D/SamplerState over
// sampler3D precisely so a compute could include it, and nothing here reaches for a 2D sampler or an
// implicit-derivative fetch.

#ifndef CLOUD_LIGHTING_COMMON_INCLUDED
#define CLOUD_LIGHTING_COMMON_INCLUDED

#include "Assets/Post Processing/Effects/Clouds/Shader Common/CloudCommon.hlsl"

/// Own constant rather than UnityCG's UNITY_PI. Identical value, but UnityCG.cginc is a fragment
/// shader header and a compute including this file has never seen it - the same reason
/// SkyViewCommon.hlsl declares its own SKYVIEW_PI.
static const float CLOUD_PI = 3.14159265359;

float cloudLightMarchLength;
float cloudLightAbsorption;
float cloudConeSpread;

/// The length the light march uses to CHOOSE ITS MIP, which is not the length it steps.
///
/// The two serve different purposes. The step length integrates optical depth, and is necessarily
/// long because the march has to cross the cloud in six samples. The mip decides how much structure
/// is visible, and choosing it from that long step samples a volume blurred past the point where a
/// lit face differs from a shadowed one - which erases self-shadowing and leaves the phase function
/// to decide brightness by itself.
float cloudLightMarchDetail;
float cloudPowderStrength;

float cloudPhaseForward;
float cloudPhaseBackward;
float cloudPhaseBlend;
float cloudSilverSpread;

/// Schneider's cone kernel: six fixed directions the light march spreads into, scaled by step index
/// so the samples describe a cone toward the sun rather than a line. A line march makes every cloud
/// self-shadow as if it were a slab; the cone is what lets light wrap around a billow.
static const float3 cloudConeKernel[6] =
{
	float3( 0.38,  0.35,  0.86),
	float3( 0.16, -0.32,  0.94),
	float3(-0.66, -0.57,  0.49),
	float3(-0.75,  0.61,  0.26),
	float3( 0.71,  0.65, -0.26),
	float3( 0.00,  0.00,  0.00)
};

float cloudHg(float cosAngle, float g)
{
	float g2 = g * g;
	return (1 - g2) / (4 * CLOUD_PI * pow(max(1e-3, 1 + g2 - 2 * g * cosAngle), 1.5));
}

/// Two-lobe Henyey-Greenstein, plus Schneider's silver lining.
///
/// The two lobes give the general forward brightening and the backward glow. Scaled by 4*pi so an
/// isotropic phase evaluates to exactly 1 rather than 1/(4*pi) - without that the whole direct term
/// was multiplied by roughly 0.02 to 0.11, a darkening no intensity slider in range could recover. A
/// phase function integrates to one over the sphere by definition; the scale only changes the units,
/// moving the 4*pi into the term where the sun's irradiance would otherwise carry it.
///
/// The silver lining is a THIRD, much tighter forward lobe, combined with max() rather than blended
/// in. That distinction is the whole point: blending a sharp lobe into the others either washes it
/// out or lifts the entire sky with it, whereas taking the larger of the two leaves the general
/// phase untouched and adds a bright rim only within a few degrees of the sun. Two lobes alone
/// cannot produce it - the effect was missing outright, not merely mistuned.
///
/// Normalised by its own peak so the intensity reads directly as "how many times the isotropic value
/// at the sun", instead of being an opaque scale on a function whose peak runs into the hundreds.
/// Passed as a parameter rather than read from a uniform because the sun, the moon and the deck's
/// underside each want a different one.
float cloudPhase(float cosAngle, float silverIntensity)
{
	float forward = cloudHg(cosAngle, cloudPhaseForward);
	float backward = cloudHg(cosAngle, -cloudPhaseBackward);
	float base = lerp(forward, backward, cloudPhaseBlend) * 4 * CLOUD_PI;

	float silverG = 0.99 - cloudSilverSpread;
	float silver = silverIntensity * cloudHg(cosAngle, silverG) / max(1e-4, cloudHg(1, silverG));

	return max(base, silver);
}

/// Optical depth between this point and the sun, cone-sampled.
///
/// Fixed at the kernel's six samples, which is Schneider's count, rather than taking a uniform step
/// count: a runtime bound would make cloudConeKernel[i] a dynamic index into a static array, which
/// the compiler either refuses or resolves expensively. [unroll] over a literal makes every index
/// compile-time constant.
float cloudLightMarch(float3 pos, float3 lightDir)
{
	const int lightSteps = 6;
	float stepSize = cloudLightMarchLength / lightSteps;
	float totalDensity = 0;

	[unroll]
	for (int i = 0; i < lightSteps; i++)
	{
		float3 samplePos = pos + lightDir * ((i + 0.5) * stepSize);
		samplePos += cloudConeKernel[i] * (cloudConeSpread * stepSize * (i + 1));
		// `cheap`: the light march skips detail erosion, where the extra fidelity is invisible but
		// the cost is the larger half of the density function.
		//
		// The mip comes from cloudLightMarchDetail, NOT from stepSize. Passing the step here asked
		// for a volume blurred to roughly a twentieth of its resolution, at which a cloud has no
		// inside and no outside - so a sample on the lit face and one deep in shadow returned nearly
		// the same density, self-shadowing vanished, and the phase function was left deciding
		// brightness on its own. That is why a backlit cloud looked brighter than the same cloud
		// front-lit, which is the wrong way round.
		totalDensity += max(0, sampleCloudDensity(samplePos, true, cloudLightMarchDetail)) * stepSize;
	}

	return totalDensity;
}

/// Beer with a powder term for the dark edge that comes from light having to scatter into a thin
/// volume before it can leave it. The reference project omits this entirely and floors its
/// transmittance with a darkness threshold instead.
///
/// The powder factor is NON-MONOTONIC in optical depth, and that has to be kept in check. It rises
/// from zero, so at full strength it is darkest exactly where the light is least obstructed - the
/// top of a cloud, which has nothing between it and the sun. At the strength this started with, the
/// product peaked around depth 0.35 and the top of a cloud came out 0.74x the brightness of its own
/// base: the shading was inverted, and clouds looked as dark from above as from below however high
/// the sun was.
///
/// Beer has to dominate for the top-to-base gradient to point the right way. Measured over a real
/// column - top at depth 0, base at 0.63 - the defaults now give the top 3.7x the base. Raising
/// powder strength flattens that back out, and past about 0.45 it inverts again.
///
/// `towardSun` is the cosine between the view ray and the sun. The powder term is only correct with
/// the sun BEHIND the viewer - Guerrilla say as much - and applying it while looking into the sun
/// darkens exactly the thin edges the silver lining is trying to light, so the two fight each other.
/// Faded out accordingly.
float cloudBeerPowder(float opticalDepth, float towardSun)
{
	float beer = exp(-opticalDepth * cloudLightAbsorption);
	float powder = 1 - exp(-opticalDepth * cloudLightAbsorption * 2);
	float strength = cloudPowderStrength * saturate(0.5 - 0.5 * towardSun);
	return beer * lerp(1.0, powder, strength);
}

#endif
