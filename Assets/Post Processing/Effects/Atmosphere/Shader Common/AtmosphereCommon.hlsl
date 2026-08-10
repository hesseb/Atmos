// Some variables and functions shared between the atmosphere shader and LUT compute shaders
// Thanks to: https://sebh.github.io/publications/egsr2020.pdf

// Declares atmosphereThickness / atmosphereRadius / planetRadius and the single definition of
// the transmittance LUT's parameterisation, shared with DrawSky.shader.
#include "TransmittanceCommon.hlsl"

// Must follow TransmittanceCommon.hlsl, which declares the planet dimensions its mapping uses.
#include "MultipleScatteringCommon.hlsl"

static const float PI = 3.14159265359;

// Rayleigh, mie and ozone parameters.
//
// Coefficients are per world unit and scale heights are in world units, so an optical depth is
// just coefficient x path length. Previously both were expressed relative to the atmosphere
// thickness and the march divided every step by it.
float3 rayleighCoefficients;
float rayleighScaleHeight;
float mieCoefficient;
float mieScaleHeight;
float mieAbsorption;
float ozonePeakAltitude;
float ozoneHalfWidth;
float3 ozoneAbsorption;

// Illuminance arriving at the top of the atmosphere from the sun.
//
// The implementation had no such term at all: absolute scale was absorbed partly by the free
// `wavelengthScale` and partly by the display-side `intensity`. With no slot for it, the only
// way to set the sky's brightness was to distort something that also carries physics.
//
// It is not free in Hillaire's formulation - L = E * sum(sigma_s * P * T_sun * T_view) ds - and
// it is what makes normalising the phase possible. See the note in `raymarch`.
float sunIlluminance;

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

// A march in progress: what has been accumulated so far, and what is left of the view ray's
// transmittance. Carried across the aerial perspective's depth slices so one ray is integrated
// once instead of 32 times from scratch.
struct ScatteringState {
	float3 luminance;
	float3 transmittance;
};

ScatteringParameters getScatteringValues(float3 rayPos) {
	ScatteringParameters scattering;

	// Altitude above the surface, in world units.
	//
	// This was normalised by the whole atmosphere thickness, which made every scale height a
	// fraction of it and every coefficient an inverse-atmosphere-thickness. Nothing could then
	// be compared against a published value without first undoing that normalisation, which is
	// most of why the constants here drifted so far from physical ones unnoticed.
	float height = length(rayPos) - planetRadius;

	// The upper clamp reproduces the previous saturate(): density stops decaying at the top of
	// the atmosphere rather than continuing to zero.
	//
	// The lower clamp stays, but it is now defensive rather than load-bearing. `raymarch` stops
	// at the ground, so the view march never samples below it; what still can is
	// `getSunTransmittance`, which follows Bruneton's convention of ignoring the ground and
	// leaving occlusion to the caller's shadow test. Without the clamp those samples would take
	// a negative altitude into exp(-h/H) and the density would explode rather than vanish.
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

// Asymmetry parameter of the Mie phase function: 0 is isotropic, ->1 is sharply forward.
// Was hardcoded at 0.8 inside getMiePhase. Exposing it is partly so it can be authored, and
// partly so the validation harness can drive it to 0, where Cornette-Shanks must reduce
// exactly to the Rayleigh phase - a cross-check that validates both functions at once.
float mieAsymmetry;

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
	float3 opticalDepth = 0;

	for (int i = 0; i < sunTransmittanceSteps; i ++) {
		// Midpoint. This advanced *before* sampling, which makes a right-Riemann sum: every
		// sample lands where the density is already lower than the interval's average, so for
		// a decreasing profile the optical depth is systematically understated - measured at
		// 13.9% here, or sun transmittance ~10% too high in blue at ground level. Midpoint
		// brings the same 40 steps to within 0.5%.
		float3 samplePos = pos + sunDir * ((i + 0.5) * stepSize);
		opticalDepth += getScatteringValues(samplePos).extinction;
	}

	// A `transmittance` accumulator used to be maintained alongside this and never returned.
	// It was not a second method either: sum-then-exponentiate and multiply-the-exponentials
	// are the same number when the step size is constant.
	return exp(-(opticalDepth * stepSize));
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

/// The body of the march, factored out so it can be resumed.
///
/// The aerial perspective needs to continue one ray across 32 depth slices, writing the running
/// totals as it passes each. Re-marching from the camera per slice is O(N^2) and, worse, makes
/// consecutive slices discretisations of *different* integrals - they disagree by more than
/// their own quadrature error, so the fog is not even monotonic in depth.
///
/// Splitting the loop out rather than copying it keeps one implementation of the physics, which
/// matters more here than usual: a second copy would be the fourth place the scattering integral
/// is written down.
void raymarchSegment(inout ScatteringState state, float3 segmentStart, float3 rayDir,
	float segmentLength, int numSteps, sampler2D transmittanceLUT, float earthShadowRadius)
{
	if (segmentLength <= 0 || numSteps <= 0) { return; }

	float3 luminance = state.luminance;
	float3 transmittance = state.transmittance;

	float stepSize = segmentLength / numSteps;
	float3 rayPos = segmentStart;

	// Sample at the midpoint of each segment rather than its start.
	//
	// The loop evaluated density at rayPos and only then advanced, which is a left-Riemann sum.
	// On a decaying profile that OVERestimates - the exact mirror of the right-Riemann bias
	// already fixed in getSunTransmittance, which underestimated. One was found and the other
	// left in place.
	//
	// It is not a small correction, because the error scales with step/H and the Mie scale
	// height is now 1.32 world units:
	//
	//   sky, 256 steps        Rayleigh +5.7%    Mie  +41.5%   ->  midpoint -0.1% / -2.2%
	//   aerial persp, 20      Rayleigh +48.6%   Mie +470.1%   ->  midpoint -3.0% / -66.7%
	//
	// Mie is the species concentrated in the lowest few units of atmosphere, so the overstated
	// term is precisely the horizon haze.
	//
	// The aerial perspective figures are still poor after this: 20 steps over a 150-unit clip
	// distance cannot resolve a 1.32-unit layer at all. That is a step-count problem, and the
	// fix is the incremental single-pass march, which buys the steps back by dropping the
	// per-slice re-march from O(N^2) to O(N).
	rayPos += rayDir * (stepSize * 0.5);

	// cosTheta = 1 looking straight at the sun, which is where a forward-scattering phase must
	// peak. Rayleigh's (1 + cos^2) is symmetric so its sign never mattered; Mie's does.
	float cosTheta = dot(rayDir, dirToSun);

	// This was `float rayleighPhaseValue = 1;`, with the correct call commented out above it.
	//
	// That single line is the reason the sky appeared to need unphysical constants. A normalised
	// phase averages 1/(4*PI) over the sphere, so substituting 1 inflates Rayleigh in-scatter by
	// 4*PI = 12.57x, and every other constant had to be bent to absorb it: coefficients ~3x
	// Earth's, and a negative red ozone absorption that adds energy.
	//
	// It cannot be corrected in sigma. In-scatter is *linear* in sigma_s but transmittance is
	// *exponential* in sigma_t, so scaling sigma by 4*PI to compensate would take blue vertical
	// optical depth from 0.27 to 3.3 and bury the planet in haze. The missing quantity is
	// illuminance, which multiplies in-scatter without touching transmittance at all.
	float rayleighPhaseValue = getRayleighPhase(cosTheta);
	float miePhase = getMiePhase(cosTheta);

	// Step through the atmosphere
	for (int stepIndex = 0; stepIndex < numSteps; stepIndex ++) {
		
		// At each step, light travelling from the sun may be scattered into the path toward the camera (in scattering)
		// Some of this in-scattered light may be scattered away as it travels toward the camera (out scattering)
		// Some light may also previously have been out-scattered while travelling through the atmosphere from the sun

		ScatteringParameters scattering = getScatteringValues(rayPos);

		// The proportion of light transmitted along the ray from the current sample point to the previous one
		float3 opticalDepth = scattering.extinction * stepSize;
		float3 sampleTransmittance = exp(-opticalDepth);
		
		// The proportion of light that reaches this point from the sun
		// float3 sunTransmittance = getSunTransmittance(rayPos, dirToSun);
		float3 sunTransmittance = sampleTransmittanceLUT(transmittanceLUT, rayPos, dirToSun);

		// Earth shadow
		if (rayIntersectSphere(rayPos, dirToSun, earthShadowRadius) > 0) {
			sunTransmittance = 0;
		}


		// Amount of light scattered in towards the camera at current sample point.
		//
		// The illuminance is new. With E = 4*PI it exactly cancels the 1/(4*PI) the restored
		// Rayleigh phase introduces, so Rayleigh's *average* over the sphere is unchanged - what
		// changes is that it now has angular structure at all, including the dark band ~90 deg
		// from the sun that a real sky has and this one did not.
		//
		// Mie is the part that actually moves. It already had its correct phase, so it was being
		// out-weighted by Rayleigh by that same 4*PI. It now rises by 12.57x relative to the
		// forward glow around the sun, which is the term a warm sunset is mostly made of.
		//
		// Note E and `intensity` cancel at this instant, so E adds no physics *by itself*. What
		// it adds is a named slot, so the phase can be normalised and sigma made physical
		// without either being silently absorbed into an art constant.
		// Multiple scattering (Hillaire 2020 section 4).
		//
		// Three things are deliberately absent from this term, and each is a consequence of the
		// isotropic assumption rather than an omission:
		//
		//  - no phase function, because orders beyond the first are taken as isotropic;
		//  - no sun transmittance, because Psi_ms was integrated with it already inside;
		//  - no earth-shadow test, and that one is the payoff. Light reaches the shadowed band
		//    by scattering, which is precisely what this term represents. That band was solid
		//    black, and treating it as black is the visible signature of single scattering.
		//
		// Total scattering coefficient, not the phase-weighted split - Psi_ms carries a radiance
		// that is the same in every direction, so both species scatter it identically.
		float3 multipleScattering = sampleMultipleScattering(rayPos, dirToSun) * multipleScatteringStrength;
		float3 scatteringCoefficient = scattering.rayleigh + scattering.mie;

		float3 inScattering = sunIlluminance *
			((scattering.rayleigh * rayleighPhaseValue + scattering.mie * miePhase) * sunTransmittance
				+ scatteringCoefficient * multipleScattering);

		// Increase the luminance by the in-scattered light.
		// The simple way would be: luminance += inScattering * transmittance * stepSize;
		// This integrates the step analytically instead, which converges at far lower step
		// counts. Same closed form as Hillaire 2020.
		float3 scatteringIntegral = inScattering * stepSize * integralFactor(opticalDepth, sampleTransmittance);
		luminance += scatteringIntegral*transmittance;
		

		// Update the transmittance along the ray from the current point in the atmosphere back to the camera
		transmittance *= sampleTransmittance;

		// Move to next sample point along ray
		rayPos += rayDir * stepSize;
	}

	state.luminance = luminance;
	state.transmittance = transmittance;
}

ScatteringResult raymarch(float3 rayPos, float3 rayDir, float rayLength, int numSteps, sampler2D transmittanceLUT, float earthShadowRadius) {
	// Stop at the ground.
	//
	// Nothing clipped the march before, so a downward ray integrated the full atmosphere chord
	// straight through the planet's interior - and because altitude is clamped at zero, those
	// interior samples were evaluated at *sea-level* density. The planet was therefore not an
	// occluder but a solid block of maximum scattering. The earth-shadow test hid most of it by
	// zeroing the sun term, which is why it never looked obviously broken.
	float dstToGround = rayIntersectSphere(rayPos, rayDir, planetRadius);
	if (dstToGround > 0) { rayLength = min(rayLength, dstToGround); }

	ScatteringState state;
	state.luminance = 0;
	state.transmittance = 1;

	raymarchSegment(state, rayPos, rayDir, rayLength, numSteps, transmittanceLUT, earthShadowRadius);

	ScatteringResult result;
	result.luminance = state.luminance;
	result.transmittance = state.transmittance;
	return result;
}


