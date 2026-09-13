// Some variables and functions shared between the atmosphere shader and LUT compute shaders
// Thanks to: https://sebh.github.io/publications/egsr2020.pdf

#ifndef ATMOSPHERE_COMMON_INCLUDED
#define ATMOSPHERE_COMMON_INCLUDED

#include "Assets/Post Processing/Effects/PBR/Atmosphere/Shader Common/TransmittanceCommon.hlsl"
#include "Assets/Post Processing/Effects/PBR/Atmosphere/Shader Common/CloudShadowCommon.hlsl"
#include "Assets/Post Processing/Effects/PBR/Atmosphere/Shader Common/MultipleScatteringCommon.hlsl"
#include "Assets/Scripts/Shader Common/Math.hlsl"

/// How much of the multiply-scattered term the cloud deck occludes, 0 to 1.
float cloudShaftMultipleScattering;
// Illuminance arriving at the top of the atmosphere from the sun. Not present in Hillaire's formulation
float sunIlluminance;
float3 dirToSun;

// Rayleigh, mie and ozone parameters.
// Coefficients are per world unit and scale heights are in world units
float3 rayleighCoefficients;
float rayleighScaleHeight;
float mieCoefficient;
float mieScaleHeight;
float mieAbsorption;
float ozonePeakAltitude;
float ozoneHalfWidth;
float3 ozoneAbsorption;

// Asymmetry parameter of the Mie phase function: 0 is isotropic, ->1 is sharply forward.
float mieAsymmetry;

///////// Internal helpers /////////////

// Cornette-Shanks, not Henyey-Greenstein. Thanks to https://www.shadertoy.com/view/slSXRW
//
// Worth flagging for the report: the pre-study's background presents HG and Schlick, so this
// is a third function that section does not cover. It is the better choice - CS is normalised,
// reduces to Rayleigh at g = 0, and unlike HG has the correct (1 + cos^2) angular dependence -
// but the report has to introduce it rather than have the code quietly disagree with the text.
float getMiePhase(float cosTheta) {
	float g = mieAsymmetry;

	const float scale = 3.0/(8.0*PI);
	float num = (1.0-g*g)*(1.0+cosTheta*cosTheta);
	float denom = (2.0+g*g)*pow(abs(1.0 + g*g - 2.0*g*cosTheta), 1.5);

	return scale*num/denom;
}

float getRayleighPhase(float cosTheta) {
	const float k = 3.0/(16.0*PI);
	return k*(1.0+cosTheta*cosTheta);
}

struct SphereChord {
	float dstTo;      // to the near intersection; 0 when the ray starts inside
	float dstThrough; // length of the chord; 0 on a miss
};

SphereChord sphereChordMiss() {
	SphereChord chord;
	chord.dstTo = 1.#INF;
	chord.dstThrough = 0;
	return chord;
}

SphereChord raySphereChord(float3 sphereCentre, float sphereRadius, float3 rayOrigin, float3 rayDir) {
	float3 offset = rayOrigin - sphereCentre;
	float a = 1; // Set to dot(rayDir, rayDir) if rayDir might not be normalized
	float b = 2 * dot(offset, rayDir);
	float c = dot (offset, offset) - sphereRadius * sphereRadius;
	float d = b * b - 4 * a * c; // Discriminant from quadratic formula

	// Number of intersections: 0 when d < 0; 1 when d = 0; 2 when d > 0
	if (d > 0) {
		float s = sqrt(d);
		float dstToSphereNear = max(0, (-b - s) / (2 * a));
		float dstToSphereFar = (-b + s) / (2 * a);

		// Ignore intersections that occur behind the ray
		if (dstToSphereFar >= 0) {
			SphereChord chord;
			chord.dstTo = dstToSphereNear;
			chord.dstThrough = dstToSphereFar - dstToSphereNear;
			return chord;
		}
	}
	return sphereChordMiss();
}

/////////// Mixed helpers /////////////

// The planet sits at the origin in geocentric mode, so every atmosphere shell is concentric with it.
SphereChord raySphereChordAtOrigin(float sphereRadius, float3 rayOrigin, float3 rayDir) {
	return raySphereChord(0, sphereRadius, rayOrigin, rayDir);
}

// From https://gamedev.stackexchange.com/questions/96459/fast-ray-sphere-collision-code.
// Returns dst to intersection of ray and sphere (works for point inside or outside of sphere)
// Returns -1 if ray does not intersect sphere
float rayIntersectSphereAtOrigin(float3 rayPos, float3 rayDir, float radius) {
	float b = dot(rayPos, rayDir);
	float c = dot(rayPos, rayPos) - radius * radius;
	if (c > 0 && b > 0) {
		return -1;
	}

	float discr = b * b - c;
	if (discr < 0) {
		return -1;
	}
	// Special case: inside sphere, use far discriminant
	if (discr > b * b) {
		return (-b + sqrt(discr));
	}
	
	return -b - sqrt(discr);
}

struct ScatteringParameters {
	float3 rayleigh;
	float mie;
	float3 extinction;
};

ScatteringParameters calcScatteringValues(float3 rayPos) {
	ScatteringParameters scattering;

	// What height are we at?
	float height = length(rayPos) - planetRadius;
	height = clamp(height, 0, atmosphereThickness);

	float rayleighDensity = exp(-height / rayleighScaleHeight);
	float mieDensity = exp(-height / mieScaleHeight);
	float ozoneDensity = saturate(1 - abs(height - ozonePeakAltitude) / ozoneHalfWidth);

	float mie = mieCoefficient * mieDensity;
	float3 rayleigh = rayleighCoefficients * rayleighDensity;

	scattering.mie = mie;
	scattering.rayleigh = rayleigh;
	scattering.extinction = mie + mieAbsorption * mieDensity + rayleigh + ozoneAbsorption * ozoneDensity;

	return scattering;
}

// (1 - exp(-t)) / t, the analytic integral of a constant source attenuated across one step,
// divided by the step length. Tends to 1 as t -> 0.
float3 integralFactor(float3 opticalDepth, float3 transmittance) {
	// We want to avoid div by zero for each channel independently. As such, we need what is effectively a multichannel bool: isNearZero
	// Per channel, if the opticalDepth is too small, it will become 1. If the opticalDepth is large enough, it becomes 0
	// isNearZero = abs(opticalDepth) <= 1e-3 ? 1 : 0;
	float3 isNearZero = step(abs(opticalDepth), 1e-3);

	// We then use isNearZero to avoid dividing by 0 on the quotient
	// This is of course a nonsense operation, but when isNearZero == 1, we discard quotient and use only series
	// So it's fine for the quotient to become a nonsense value. here we're just avoiding it becoming a NaN
	float3 quotient = (1.0 - transmittance) / (opticalDepth + isNearZero);
	// We want to use this series term instead of the quotient due to precision issues which the quotient doesn't handle well at low opticalDepths
	float3 series = 1.0 - opticalDepth * (0.5 - opticalDepth * (1.0 / 6.0));

	// Despite being a lerp, this is actually selecting between two terms: quotient or series
	// We want quotient when opticalDepth is large enough and series otherwise
	// isNearZero is always only 0 or 1 depending on that same condition, meaning it picks the right term per channel
	return lerp(quotient, series, isNearZero);
}

struct ScatteringState {
	float3 luminance;
	float3 transmittance;
};

/// The body of the march, factored out so the aerial perspective can resume one ray across its
/// depth slices instead of re-marching from the camera per slice.
void marchScatteringSegment(inout ScatteringState state, float3 segmentStart, float3 rayDir,
	float segmentLength, int numSteps, sampler2D transmittanceLUT, float earthShadowRadius)
{
	if (segmentLength <= 0 || numSteps <= 0)
	{
		return;
	}

	// Setup
	float3 luminance = state.luminance;
	float3 transmittance = state.transmittance;
	float stepSize = segmentLength / numSteps;
	float3 rayPos = segmentStart;
	// Midpoint sampling
	rayPos += rayDir * (stepSize * 0.5);

	float cosTheta = dot(rayDir, dirToSun);
	float rayleighPhaseValue = getRayleighPhase(cosTheta);
	float miePhase = getMiePhase(cosTheta);

	// The raymarching itself
	for (int stepIndex = 0; stepIndex < numSteps; stepIndex ++) {
		// Setup
		ScatteringParameters scattering = calcScatteringValues(rayPos);
		float3 opticalDepth = scattering.extinction * stepSize;
		float3 sampleTransmittance = exp(-opticalDepth);
		float3 sunTransmittance = sampleTransmittanceLUT(transmittanceLUT, rayPos, dirToSun);

		if (earthShadowRadius > 0 && rayIntersectSphereAtOrigin(rayPos, dirToSun, earthShadowRadius) > 0) {
			sunTransmittance = 0;
		}

		float cloudVisibility = cloudSunVisibility(rayPos, dirToSun);
		sunTransmittance *= cloudVisibility;

		float3 multipleScattering = sampleMultipleScattering(rayPos, dirToSun) * multipleScatteringStrength;
		multipleScattering *= lerp(1, cloudVisibility, cloudShaftMultipleScattering);

		float3 scatteringCoefficient = scattering.rayleigh + scattering.mie;

		float3 inScattering = sunIlluminance *
			((scattering.rayleigh * rayleighPhaseValue + scattering.mie * miePhase) * sunTransmittance
				+ scatteringCoefficient * multipleScattering);

		// Analytic per-step integration rather than inScattering * transmittance * stepSize, which
		// needs far more steps to converge. Same closed form as Hillaire 2020.
		float3 scatteringIntegral = inScattering * stepSize * integralFactor(opticalDepth, sampleTransmittance);
		
		// The values we care about
		luminance += scatteringIntegral * transmittance;
		transmittance *= sampleTransmittance;
		
		// Walk the ray
		rayPos += rayDir * stepSize;
	}

	// Return values
	state.luminance = luminance;
	state.transmittance = transmittance;
}

///////// External entry points /////////////////

ScatteringState marchScattering(float3 rayPos, float3 rayDir, float rayLength, int numSteps, sampler2D transmittanceLUT, float earthShadowRadius) {
	float dstToGround = rayIntersectSphereAtOrigin(rayPos, rayDir, planetRadius);
	
	if (dstToGround > 0)
	{
		rayLength = min(rayLength, dstToGround);
	}

	ScatteringState state;
	state.luminance = 0;
	state.transmittance = 1;

	marchScatteringSegment(state, rayPos, rayDir, rayLength, numSteps, transmittanceLUT, earthShadowRadius);
	return state;
}

/// Transmittance from `pos` along `rayDir` to the top of the atmosphere.
float3 marchTransmittance(float3 pos, float3 rayDir) {
	const int transmittanceSteps = 40;

	float rayLength = raySphereChordAtOrigin(atmosphereRadius, pos, rayDir).dstThrough;

	float stepSize = rayLength / transmittanceSteps;
	float3 extinctionSum = 0;

	for (int i = 0; i < transmittanceSteps; i ++) {
		float3 samplePos = pos + rayDir * ((i + 0.5) * stepSize);
		extinctionSum += calcScatteringValues(samplePos).extinction;
	}

	// We multiply by stepSize to get the optical depth out of the extinction sum
	return exp(-(extinctionSum * stepSize));
}
#endif
