using UnityEngine;

/// <summary>
/// A C# mirror of the atmosphere's density, phase and tone-map functions.
///
/// Its purpose is to give the validation checks something to evaluate that is independent of
/// the GPU, so properties like "extinction is positive everywhere" and "the phase integrates
/// to one" can be tested at 10,000 sample points without a readback.
///
/// **It cannot prove the shader is correct.** It is a transcription, and a transcription can
/// diverge from what it transcribes. That gap is closed by exactly one check -
/// `CheckTransmittanceLUT` reads the shader's own LUT and compares it against this file's
/// quadrature. If the two ever disagree, the mirror is stale and every other check is
/// suspect.
///
/// Mirrors `getScatteringValues`, `getRayleighPhase`, `getMiePhase` and `toneMap` in
/// `AtmosphereCommon.hlsl` / `DrawAtmosphereCommon.hlsl`. Keep it in step with them.
/// </summary>
static class AtmosphereReference
{
	/// <summary>`rayleighCoefficients = (wavelengthScale / lambda)^4`, per channel.</summary>
	public static Vector3 RayleighCoefficients(AtmosphereEffect a)
	{
		return new Vector3(
			Mathf.Pow(a.wavelengthScale / a.wavelengthsRGB.x, 4),
			Mathf.Pow(a.wavelengthScale / a.wavelengthsRGB.y, 4),
			Mathf.Pow(a.wavelengthScale / a.wavelengthsRGB.z, 4));
	}

	/// <summary>The ozone term as it reaches the shader, including the undocumented 0.1.</summary>
	public static Vector3 OzoneAbsorption(AtmosphereEffect a)
	{
		return a.ozoneAbsorption * a.ozoneStrength * 0.1f;
	}

	/// <summary>
	/// Extinction at an altitude in world units. Mirrors `getScatteringValues`, including its
	/// normalisation of altitude by the whole atmosphere thickness rather than by a scale
	/// height in metres.
	/// </summary>
	public static Vector3 Extinction(AtmosphereEffect a, float height)
	{
		float h01 = Mathf.Clamp01(height / a.atmosphereThickness);

		float rayleighDensity = Mathf.Exp(-h01 / a.rayleighDensityAvg);
		float mieDensity = Mathf.Exp(-h01 / a.mieDensityAvg);
		float ozoneDensity = Mathf.Clamp01(1f - Mathf.Abs(a.ozonePeakDensityAltitude - h01) * a.ozoneDensityFalloff);

		Vector3 rayleigh = RayleighCoefficients(a) * rayleighDensity;
		float mie = a.mieCoefficient * mieDensity;

		return new Vector3(mie, mie, mie)
			+ Vector3.one * (a.mieAbsorption * mieDensity)
			+ rayleigh
			+ OzoneAbsorption(a) * ozoneDensity;
	}

	/// <summary>Rayleigh extinction alone, for cross-checking against the beta*H closed form.</summary>
	public static Vector3 RayleighExtinction(AtmosphereEffect a, float height)
	{
		float h01 = Mathf.Clamp01(height / a.atmosphereThickness);
		return RayleighCoefficients(a) * Mathf.Exp(-h01 / a.rayleighDensityAvg);
	}

	/// <summary>
	/// Optical depth for a vertical path from the surface to the top of the atmosphere.
	///
	/// Divided by `atmosphereThickness` because the shader's march scales every step that way
	/// (`scaledStepSize = stepSize / atmosphereThickness`), which makes the coefficients
	/// inverse-atmosphere-thicknesses rather than inverse world units.
	/// </summary>
	public static Vector3 VerticalOpticalDepth(AtmosphereEffect a, int steps, bool rayleighOnly = false)
	{
		Vector3 total = Vector3.zero;
		float dh = a.atmosphereThickness / steps;

		for (int i = 0; i < steps; i++)
		{
			float h = (i + 0.5f) * dh;
			total += (rayleighOnly ? RayleighExtinction(a, h) : Extinction(a, h)) * dh;
		}
		return total / a.atmosphereThickness;
	}

	/// <summary>
	/// The same integral as `getSunTransmittance` actually computes it: 40 steps, sampling
	/// *after* advancing rather than at the midpoint, which is a right-Riemann sum and
	/// systematically underestimates optical depth for a decreasing density profile.
	/// </summary>
	public static Vector3 VerticalOpticalDepthRightRiemann(AtmosphereEffect a, int steps)
	{
		Vector3 total = Vector3.zero;
		float dh = a.atmosphereThickness / steps;

		for (int i = 1; i <= steps; i++)
		{
			total += Extinction(a, i * dh) * dh;
		}
		return total / a.atmosphereThickness;
	}

	// ------------------------------------------------------------------ phases

	public static double RayleighPhase(float cosTheta)
	{
		const double k = 3.0 / (16.0 * Mathf.PI);
		return k * (1.0 + cosTheta * cosTheta);
	}

	public static double MiePhase(float cosTheta, float g)
	{
		double scale = 3.0 / (8.0 * Mathf.PI);
		double num = (1.0 - g * g) * (1.0 + cosTheta * cosTheta);
		double denom = (2.0 + g * g) * System.Math.Pow(System.Math.Abs(1.0 + g * g - 2.0 * g * cosTheta), 1.5);
		return scale * num / denom;
	}

	// ------------------------------------------------------------------ tone map

	public static float ToneMap(AtmosphereEffect a, float value)
	{
		float v = value * a.intensity;
		v = 0.5f + a.contrast * (v - 0.5f);
		v = v * (1f + v / (a.whitePoint * a.whitePoint)) / (1f + v);
		return SmoothMax(v, -0.05f, 0.05f);
	}

	/// <summary>
	/// Inverts the tone map: what input produces this displayed value. Used to report the
	/// usable input band, which every change to absolute energy has to land inside.
	/// </summary>
	public static float InputForDisplay(AtmosphereEffect a, float displayed)
	{
		float w2 = a.whitePoint * a.whitePoint;
		float b = w2 * (1f - displayed);
		float v = (-b + Mathf.Sqrt(b * b + 4f * w2 * displayed)) * 0.5f;
		return (0.5f + (v - 0.5f) / a.contrast) / a.intensity;
	}

	static float SmoothMax(float a, float b, float k)
	{
		k = -Mathf.Abs(k);
		float h = Mathf.Clamp01((b - a + k) / (2f * k));
		return a * h + b * (1f - h) - k * h * (1f - h);
	}
}
