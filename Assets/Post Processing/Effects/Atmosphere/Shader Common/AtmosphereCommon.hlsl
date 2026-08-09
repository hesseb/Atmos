// Some variables and functions shared between the atmosphere shader and LUT compute shaders
// Thanks to: https://sebh.github.io/publications/egsr2020.pdf

// Declares atmosphereThickness / atmosphereRadius / planetRadius and the single definition of
// the transmittance LUT's parameterisation, shared with DrawSky.shader.
#include "TransmittanceCommon.hlsl"

static const float PI = 3.14159265359;

// Rayleigh, mie, and ozone parameters
float3 rayleighCoefficients;
float rayleighDensityAvg;
float mieCoefficient;
float mieDensityAvg;
float mieAbsorption;
float ozonePeakDensityAltitude;
float ozoneDensityFalloff;
float3 ozoneAbsorption;

// Other
float3 dirToSun;
float terrestrialClipDst;

struct ScatteringParameters {
	float3 rayleigh;
	float mie;
	float3 extinction;
};

struct ScatteringResult {
	float3 luminance;
	float3 transmittance;
};

ScatteringParameters getScatteringValues(float3 rayPos) {
	ScatteringParameters scattering;

	float height = length(rayPos - 0) - planetRadius;
	float height01 = saturate(height / atmosphereThickness);

	float rayleighDensity = exp(-height01 / rayleighDensityAvg);
	float mieDensity = exp(-height01 / mieDensityAvg);
	float ozoneDensity = saturate(1 - abs(ozonePeakDensityAltitude - height01) * ozoneDensityFalloff);

	float mie = mieCoefficient * mieDensity;
	float3 rayleigh = rayleighCoefficients * rayleighDensity;

	scattering.mie = mie;
	scattering.rayleigh = rayleigh;
	
	scattering.extinction = mie + mieAbsorption * mieDensity + rayleigh + ozoneAbsorption * ozoneDensity;
	return scattering;
}

// Thanks to https://www.shadertoy.com/view/slSXRW
float getMiePhase(float cosTheta) {
	const float g = 0.8;
	const float scale = 3.0/(8.0*PI);
	
	float num = (1.0-g*g)*(1.0+cosTheta*cosTheta);
	float denom = (2.0+g*g)*pow(abs(1.0 + g*g - 2.0*g*cosTheta), 1.5);
	
	return scale*num/denom;
}

float getRayleighPhase(float cosTheta) {
	const float k = 3.0/(16.0*PI);
	return k*(1.0+cosTheta*cosTheta);
}

// Returns vector (dstToSphere, dstThroughSphere)
// If ray origin is inside sphere, dstToSphere = 0
// If ray misses sphere, dstToSphere = infinity; dstThroughSphere = 0
float2 raySphere(float3 sphereCentre, float sphereRadius, float3 rayOrigin, float3 rayDir) {
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
			return float2(dstToSphereNear, dstToSphereFar - dstToSphereNear);
		}
	}
	// Ray did not intersect sphere
	return float2(1.#INF, 0);
}

// From https://gamedev.stackexchange.com/questions/96459/fast-ray-sphere-collision-code.
// Returns dst to intersection of ray and sphere (works for point inside or outside of sphere)
// Returns -1 if ray does not intersect sphere
float rayIntersectSphere(float3 rayPos, float3 rayDir, float radius) {
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


float3 getSunTransmittance(float3 pos, float3 sunDir) {
	const int sunTransmittanceSteps = 40;
	
	float2 atmoHitInfo = raySphere(0, atmosphereRadius, pos, sunDir);
	float rayLength = atmoHitInfo.y;

	float stepSize = rayLength / sunTransmittanceSteps;
	float3 transmittance = 1;
	float3 opticalDepth = 0;

	for (int i = 0; i < sunTransmittanceSteps; i ++) {
		pos += sunDir * stepSize;
		ScatteringParameters scattering = getScatteringValues(pos);
		
		transmittance *= exp(-scattering.extinction / atmosphereThickness * stepSize);
		opticalDepth += scattering.extinction;

	}
	return exp(-(opticalDepth / atmosphereThickness * stepSize));
}

// (1 - exp(-t)) / t, the analytic integral of a constant source attenuated across one step,
// divided by the step length. Tends to 1 as t -> 0.
//
// This replaces `(S - S*T) / max(0.0001, extinction)`, which was **already clamping** at the
// shipped parameters: extinction falls below 1e-4 above h01 = 0.856 in red and 0.95 in blue,
// so the clamp fires across the top 14% of the atmosphere and understates in-scatter there by
// up to 5x. That was invisible only because the density there is ~1e-5 of sea level.
//
// It stops being invisible with physically scaled heights, where extinction at the top is
// ~1e-7: three orders below the clamp, understating in-scatter by ~1000x. Per channel, so red
// fails before blue and the result desaturates rather than simply darkening.
//
// Near zero the quotient itself is the problem: both terms vanish and float32 cancellation
// leaves only a few significant digits. The series is exact to 4e-11 at the switchover.
float3 integralFactor(float3 opticalDepth, float3 transmittance) {
	float3 nearZero = step(abs(opticalDepth), 1e-3);

	// The +nearZero only keeps the discarded branch from dividing by zero; where this branch
	// is used the denominator is at least 1e-3.
	float3 quotient = (1.0 - transmittance) / (opticalDepth + nearZero);
	float3 series = 1.0 - opticalDepth * (0.5 - opticalDepth * (1.0 / 6.0));

	return lerp(quotient, series, nearZero);
}

ScatteringResult raymarch(float3 rayPos, float3 rayDir, float rayLength, int numSteps, sampler2D transmittanceLUT, float earthShadowRadius) {
	float3 luminance = 0;
	float3 transmittance = 1;

	float stepSize = rayLength / numSteps;
	float scaledStepSize = stepSize / atmosphereThickness;

	float cosTheta = dot(rayDir, dirToSun);
	//float rayleighPhaseValue = getRayleighPhase(-cosTheta);
	float rayleighPhaseValue = 1;
	float miePhase = getMiePhase(cosTheta);

	// Step through the atmosphere
	for (int stepIndex = 0; stepIndex < numSteps; stepIndex ++) {
		
		// At each step, light travelling from the sun may be scattered into the path toward the camera (in scattering)
		// Some of this in-scattered light may be scattered away as it travels toward the camera (out scattering)
		// Some light may also previously have been out-scattered while travelling through the atmosphere from the sun

		ScatteringParameters scattering = getScatteringValues(rayPos);

		// The proportion of light transmitted along the ray from the current sample point to the previous one
		float3 opticalDepth = scattering.extinction * scaledStepSize;
		float3 sampleTransmittance = exp(-opticalDepth);
		
		// The proportion of light that reaches this point from the sun
		// float3 sunTransmittance = getSunTransmittance(rayPos, dirToSun);
		float3 sunTransmittance = sampleTransmittanceLUT(transmittanceLUT, rayPos, dirToSun);

		// Earth shadow
		if (rayIntersectSphere(rayPos, dirToSun, earthShadowRadius) > 0) {
			sunTransmittance = 0;
		}


		// Amount of light scattered in towards the camera at current sample point
		float3 inScattering = (scattering.rayleigh * rayleighPhaseValue + scattering.mie * miePhase) * sunTransmittance;

		// Increase the luminance by the in-scattered light.
		// The simple way would be: luminance += inScattering * transmittance * scaledStepSize;
		// This integrates the step analytically instead, which converges at far lower step
		// counts. Same closed form as Hillaire 2020.
		float3 scatteringIntegral = inScattering * scaledStepSize * integralFactor(opticalDepth, sampleTransmittance);
		luminance += scatteringIntegral*transmittance;
		

		// Update the transmittance along the ray from the current point in the atmosphere back to the camera
		transmittance *= sampleTransmittance;

		// Move to next sample point along ray
		rayPos += rayDir * stepSize;
	}
	
	ScatteringResult result;
	result.luminance = luminance;
	result.transmittance = transmittance;
	return result;
}

