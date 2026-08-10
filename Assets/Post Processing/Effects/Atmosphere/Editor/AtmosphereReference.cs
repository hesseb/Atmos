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
	/// Extinction per world unit at an altitude in world units. Mirrors `getScatteringValues`.
	///
	/// The shader takes scale heights and coefficients already converted to world units, so
	/// this applies the same conversion the effect does on the way to the GPU.
	/// </summary>
	public static Vector3 Extinction(AtmosphereEffect a, float height)
	{
		float h = Mathf.Clamp(height, 0f, a.atmosphereThickness);

		float rayleighDensity = Mathf.Exp(-h / (a.rayleighDensityAvg * a.atmosphereThickness));
		float mieDensity = Mathf.Exp(-h / (a.mieDensityAvg * a.atmosphereThickness));

		float ozonePeak = a.ozonePeakDensityAltitude * a.atmosphereThickness;
		float ozoneHalfWidth = a.atmosphereThickness / Mathf.Max(1e-4f, a.ozoneDensityFalloff);
		float ozoneDensity = Mathf.Clamp01(1f - Mathf.Abs(h - ozonePeak) / ozoneHalfWidth);

		Vector3 rayleigh = RayleighCoefficients(a) * (rayleighDensity / a.atmosphereThickness);
		float mie = a.mieCoefficient * mieDensity / a.atmosphereThickness;

		return new Vector3(mie, mie, mie)
			+ Vector3.one * (a.mieAbsorption * mieDensity / a.atmosphereThickness)
			+ rayleigh
			+ OzoneAbsorption(a) * (ozoneDensity / a.atmosphereThickness);
	}

	// ---------------------------------------------------------------- Bruneton mapping mirror

	static float LutUnitToSubUv(float unit, float size) => 0.5f / size + unit * (1f - 1f / size);
	static float LutSubUvToUnit(float uv, float size) => (uv - 0.5f / size) / (1f - 1f / size);

	/// <summary>Mirror of `transmittanceLutUv`. Radius and cosine to LUT coordinates.</summary>
	public static Vector2 TransmittanceLutUv(AtmosphereEffect a, float radius, float cosZenith)
	{
		float rt = a.bodyRadius + a.atmosphereThickness;
		radius = Mathf.Clamp(radius, a.bodyRadius, rt);

		float h = Mathf.Sqrt(Mathf.Max(0f, rt * rt - a.bodyRadius * a.bodyRadius));
		float rho = Mathf.Sqrt(Mathf.Max(0f, radius * radius - a.bodyRadius * a.bodyRadius));

		float discriminant = radius * radius * (cosZenith * cosZenith - 1f) + rt * rt;
		float d = Mathf.Max(0f, -radius * cosZenith + Mathf.Sqrt(Mathf.Max(0f, discriminant)));

		float dMin = rt - radius;
		float dMax = rho + h;

		float xMu = dMax > dMin ? (d - dMin) / (dMax - dMin) : 0f;
		float xR = h > 0f ? rho / h : 0f;

		return new Vector2(LutUnitToSubUv(Mathf.Clamp01(xMu), a.transmittanceLUTSize.x),
			LutUnitToSubUv(Mathf.Clamp01(xR), a.transmittanceLUTSize.y));
	}

	/// <summary>Mirror of `transmittanceLutParams`. LUT coordinates back to radius and cosine.</summary>
	public static void TransmittanceLutParams(AtmosphereEffect a, Vector2 uv, out float radius, out float cosZenith)
	{
		float rt = a.bodyRadius + a.atmosphereThickness;
		float unitX = LutSubUvToUnit(uv.x, a.transmittanceLUTSize.x);
		float unitY = LutSubUvToUnit(uv.y, a.transmittanceLUTSize.y);

		float h = Mathf.Sqrt(Mathf.Max(0f, rt * rt - a.bodyRadius * a.bodyRadius));
		float rho = h * unitY;
		radius = Mathf.Sqrt(rho * rho + a.bodyRadius * a.bodyRadius);

		float dMin = rt - radius;
		float dMax = rho + h;
		float d = dMin + unitX * (dMax - dMin);

		cosZenith = d <= 0f ? 1f : (h * h - rho * rho - d * d) / (2f * radius * d);
		cosZenith = Mathf.Clamp(cosZenith, -1f, 1f);
	}

	/// <summary>The horizon cosine at a radius: the lowest direction that still misses the ground.</summary>
	public static float HorizonCosine(AtmosphereEffect a, float radius)
	{
		return -Mathf.Sqrt(Mathf.Max(0f, 1f - a.bodyRadius * a.bodyRadius / (radius * radius)));
	}

	/// <summary>
	/// Mie extinction alone, scattering plus absorption.
	///
	/// Needed on its own because Mie is only ~4% of blue extinction, so any error in it is
	/// almost invisible in a total-blue figure - and Mie is the species with the small scale
	/// height, hence the one a step count actually fails to resolve.
	/// </summary>
	public static float MieExtinction(AtmosphereEffect a, float height)
	{
		float h = Mathf.Clamp(height, 0f, a.atmosphereThickness);
		float density = Mathf.Exp(-h / (a.mieDensityAvg * a.atmosphereThickness));
		return (a.mieCoefficient + a.mieAbsorption) * density / a.atmosphereThickness;
	}

	/// <summary>Mie-only vertical optical depth. `leftRiemann` reproduces the pre-midpoint
	/// sampling, so the two can be reported side by side.</summary>
	public static double MieVerticalOpticalDepth(AtmosphereEffect a, int steps, bool leftRiemann = false)
	{
		double total = 0;
		double dh = a.atmosphereThickness / (double)steps;

		for (int i = 0; i < steps; i++)
		{
			float h = (float)((leftRiemann ? i : i + 0.5) * dh);
			total += MieExtinction(a, h) * dh;
		}

		return total;
	}

	/// <summary>Rayleigh extinction alone, for cross-checking against the beta*H closed form.</summary>
	public static Vector3 RayleighExtinction(AtmosphereEffect a, float height)
	{
		float h = Mathf.Clamp(height, 0f, a.atmosphereThickness);
		float scaleHeight = a.rayleighDensityAvg * a.atmosphereThickness;
		return RayleighCoefficients(a) * (Mathf.Exp(-h / scaleHeight) / a.atmosphereThickness);
	}

	/// <summary>Optical depth for a vertical path from the surface to the top of the
	/// atmosphere, by midpoint quadrature.</summary>
	public static Vector3 VerticalOpticalDepth(AtmosphereEffect a, int steps, bool rayleighOnly = false)
	{
		// Accumulated in double. At 200k steps a float sum reaches ~78 while each term is
		// ~4e-3, so every addition truncates in the same direction and the running total ends
		// systematically low - 0.70811 against a true 0.70853, which is 40x the tolerance this
		// is checked at. The physics is fine; the accumulator was not.
		double r = 0, g = 0, b = 0;
		double dh = a.atmosphereThickness / (double)steps;

		for (int i = 0; i < steps; i++)
		{
			float h = (float)((i + 0.5) * dh);
			Vector3 e = rayleighOnly ? RayleighExtinction(a, h) : Extinction(a, h);
			r += e.x * dh; g += e.y * dh; b += e.z * dh;
		}

		// No division by thickness: Extinction is now per world unit, so the sum of
		// sigma * dh IS the optical depth.
		return new Vector3((float)r, (float)g, (float)b);
	}

	/// <summary>
	/// The same integral as `getSunTransmittance` actually computes it: 40 steps, sampling
	/// *after* advancing rather than at the midpoint, which is a right-Riemann sum and
	/// systematically underestimates optical depth for a decreasing density profile.
	/// </summary>
	public static Vector3 VerticalOpticalDepthRightRiemann(AtmosphereEffect a, int steps)
	{
		double r = 0, g = 0, b = 0;
		double dh = a.atmosphereThickness / (double)steps;

		for (int i = 1; i <= steps; i++)
		{
			Vector3 e = Extinction(a, (float)(i * dh));
			r += e.x * dh; g += e.y * dh; b += e.z * dh;
		}

		// No division by thickness: Extinction is now per world unit, so the sum of
		// sigma * dh IS the optical depth.
		return new Vector3((float)r, (float)g, (float)b);
	}

	/// <summary>
	/// The integral as `raymarch` computed it before midpoint sampling: sampling at the *start*
	/// of each segment, which is a left-Riemann sum and systematically OVERestimates optical
	/// depth for a decreasing profile - the exact mirror of the right-Riemann bias above.
	///
	/// Kept so the harness can report the size of what was corrected rather than asserting it
	/// from a comment. The error scales with step/H, so it is worst for Mie, whose scale height
	/// is smallest and whose density sits lowest - i.e. along the horizon paths where it showed.
	/// </summary>
	public static Vector3 VerticalOpticalDepthLeftRiemann(AtmosphereEffect a, int steps)
	{
		double r = 0, g = 0, b = 0;
		double dh = a.atmosphereThickness / (double)steps;

		for (int i = 0; i < steps; i++)
		{
			Vector3 e = Extinction(a, (float)(i * dh));
			r += e.x * dh; g += e.y * dh; b += e.z * dh;
		}

		return new Vector3((float)r, (float)g, (float)b);
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
