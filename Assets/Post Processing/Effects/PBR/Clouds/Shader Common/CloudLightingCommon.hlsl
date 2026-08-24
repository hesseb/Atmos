// The cloud's optical model - phase function, Beer-Powder, cone-sampled light march - shared by the
// view-ray march and the downward-radiance bake so both agree on the same physics. Each consumer
// still assembles its own radiance from sun/sky/moon terms; that assembly lives elsewhere. Compute-safe.

#ifndef CLOUD_LIGHTING_COMMON_INCLUDED
#define CLOUD_LIGHTING_COMMON_INCLUDED

#include "Assets/Post Processing/Effects/PBR/Clouds/Shader Common/CloudCommon.hlsl"

/// Own constant since UnityCG's UNITY_PI isn't visible to a compute shader including this file.
static const float CLOUD_PI = 3.14159265359;

float cloudLightMarchLength;
float cloudLightAbsorption;
float cloudConeSpread;

/// Mip-selection length for the light march, kept separate from its (much longer) step length so
/// self-shadowing detail doesn't get blurred away.
float cloudLightMarchDetail;
float cloudPowderStrength;

float cloudPhaseForward;
float cloudPhaseBackward;
float cloudPhaseBlend;
float cloudSilverSpread;

/// Schneider's cone kernel: six directions the light march spreads into (scaled by step index) so
/// light can wrap around a billow instead of self-shadowing like a flat slab.
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

/// Two-lobe Henyey-Greenstein (forward + backward), scaled by 4*pi so isotropic evaluates to 1, plus
/// Schneider's silver lining as a third, tighter forward lobe combined via max() rather than blended -
/// so it adds a bright rim near the sun without washing out or lifting the general phase. Silver
/// intensity is normalised by its own peak and passed per-caller since sun/moon/underside each differ.
float cloudPhase(float cosAngle, float silverIntensity)
{
	float forward = cloudHg(cosAngle, cloudPhaseForward);
	float backward = cloudHg(cosAngle, -cloudPhaseBackward);
	float base = lerp(forward, backward, cloudPhaseBlend) * 4 * CLOUD_PI;

	float silverG = 0.99 - cloudSilverSpread;
	float silver = silverIntensity * cloudHg(cosAngle, silverG) / max(1e-4, cloudHg(1, silverG));

	return max(base, silver);
}

/// Optical depth to the sun, cone-sampled over a fixed, unrolled 6 steps (Schneider's count) so each
/// index into cloudConeKernel stays a compile-time constant.
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
		// `cheap` skips detail erosion (invisible here, the expensive half of density); mip comes
		// from cloudLightMarchDetail, not stepSize, so self-shadowing isn't blurred away.
		totalDensity += max(0, sampleCloudDensity(samplePos, true, cloudLightMarchDetail)) * stepSize;
	}

	return totalDensity;
}

/// Beer-Lambert with a powder term for the dark edge of a thin volume; the reference project omits
/// this and uses a darkness threshold instead. Powder is non-monotonic in optical depth, so its
/// strength is kept low enough that Beer still dominates the top-to-base gradient. Only correct with
/// the sun behind the viewer, so it's faded out toward the sun to avoid fighting the silver lining.
float cloudBeerPowder(float opticalDepth, float towardSun)
{
	float beer = exp(-opticalDepth * cloudLightAbsorption);
	float powder = 1 - exp(-opticalDepth * cloudLightAbsorption * 2);
	float strength = cloudPowderStrength * saturate(0.5 - 0.5 * towardSun);
	return beer * lerp(1.0, powder, strength);
}

#endif
