using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Checks the atmosphere against things that are true independently of it.
///
/// This exists because the physics is about to change substantially, and "it looks right" is
/// not a claim the report can make. Every check here produces a number with a ground truth:
/// an analytic integral, a normalisation identity, a monotonicity property, or a closed form
/// the GPU result must reproduce.
///
/// Deliberately a menu item rather than NUnit, matching `Testbed/Benchmark/Run Stats
/// Self-Test`: a test asmdef cannot reference the predefined Assembly-CSharp where
/// AtmosphereEffect lives, and an Editor folder compiles into Assembly-CSharp-Editor which
/// does not carry the NUnit references either.
///
/// **What each half proves.** The arithmetic checks validate the *model* - they evaluate a C#
/// mirror of the shader's density and phase functions. They cannot catch the C# mirror and the
/// HLSL disagreeing. The GPU readback check is what closes that gap: it compares the shader's
/// own transmittance LUT against a closed form, so a divergence between mirror and shader
/// shows up there.
/// </summary>
static class AtmosphereValidation
{
	static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

	[MenuItem("Testbed/Atmosphere/Validate")]
	static void Validate()
	{
		AtmosphereEffect atmosphere = FindAtmosphere();
		if (atmosphere == null)
		{
			Debug.LogError("[Atmosphere] no AtmosphereEffect asset found.");
			return;
		}

		var report = new StringBuilder();
		int failures = 0;

		report.Append("# Atmosphere validation\n\n");
		AppendParameters(report, atmosphere);

		failures += CheckPhaseNormalisation(report);
		failures += CheckWavelengthLaw(report, atmosphere);
		failures += CheckExtinctionPositive(report, atmosphere);
		failures += CheckOpticalDepth(report, atmosphere);
		failures += CheckToneMapBand(report, atmosphere);
		failures += CheckSunDisc(report, atmosphere);
		failures += CheckTransmittanceLUT(report, atmosphere);

		report.Append(failures == 0
			? "\n**All checks passed.**\n"
			: $"\n**{failures} check(s) FAILED.**\n");

		if (failures == 0) { Debug.Log(report.ToString()); }
		else { Debug.LogError(report.ToString()); }
	}

	static void AppendParameters(StringBuilder report, AtmosphereEffect a)
	{
		report.Append("| parameter | value |\n|---|---|\n")
			.Append($"| planet radius | {F(a.bodyRadius)} |\n")
			.Append($"| atmosphere thickness | {F(a.atmosphereThickness)} |\n")
			.Append($"| thickness / radius | {F(a.atmosphereThickness / a.bodyRadius)} (Earth 0.0157) |\n")
			.Append($"| Rayleigh scale height | {F(a.rayleighDensityAvg * a.atmosphereThickness)} units |\n")
			.Append($"| Mie scale height | {F(a.mieDensityAvg * a.atmosphereThickness)} units |\n")
			.Append($"| scale height ratio | {F(a.rayleighDensityAvg / a.mieDensityAvg)} : 1 (physical 6.67 : 1) |\n")
			.Append($"| sky / aerial steps | {a.numSkyScatteringSteps} / {a.numAerialScatteringSteps} |\n\n");
	}

	// ------------------------------------------------------------------ arithmetic

	/// <summary>
	/// Both phase functions must integrate to 1 over the sphere, or "in-scattering" is not
	/// conserving the energy it claims to redistribute. Rayleigh is exact analytically;
	/// Cornette-Shanks is checked by quadrature.
	///
	/// The third assertion is free and validates both at once: Cornette-Shanks at g = 0
	/// reduces algebraically to the Rayleigh phase.
	/// </summary>
	static int CheckPhaseNormalisation(StringBuilder report)
	{
		report.Append("## Phase normalisation\n\n");

		double rayleigh = IntegrateOverSphere(mu => AtmosphereReference.RayleighPhase(mu));
		double mie = IntegrateOverSphere(mu => AtmosphereReference.MiePhase(mu, 0.8f));

		double worstReduction = 0;
		for (int i = 0; i <= 200; i++)
		{
			double mu = -1.0 + 2.0 * i / 200.0;
			worstReduction = System.Math.Max(worstReduction,
				System.Math.Abs(AtmosphereReference.MiePhase((float)mu, 0f) - AtmosphereReference.RayleighPhase((float)mu)));
		}

		int failures = 0;
		failures += Assert(report, "Rayleigh integrates to 1", rayleigh, 1.0, 1e-6);
		failures += Assert(report, "Cornette-Shanks (g=0.8) integrates to 1", mie, 1.0, 1e-4);
		failures += Assert(report, "Cornette-Shanks at g=0 equals Rayleigh", worstReduction, 0.0, 1e-7);
		return failures;
	}

	/// <summary>
	/// The report states Rayleigh scattering goes as 1/lambda^4. That makes beta*lambda^4 a
	/// constant across channels - a property of the numbers, not of any transcription.
	/// </summary>
	static int CheckWavelengthLaw(StringBuilder report, AtmosphereEffect a)
	{
		report.Append("\n## Rayleigh wavelength law\n\n");

		Vector3 beta = AtmosphereReference.RayleighCoefficients(a);
		Vector3 l = a.wavelengthsRGB;
		double r = beta.x * System.Math.Pow(l.x, 4);
		double g = beta.y * System.Math.Pow(l.y, 4);
		double b = beta.z * System.Math.Pow(l.z, 4);

		report.Append($"- beta = ({F(beta.x)}, {F(beta.y)}, {F(beta.z)}) at lambda = " +
			$"({F(l.x)}, {F(l.y)}, {F(l.z)}) nm\n");

		return Assert(report, "beta * lambda^4 constant across channels",
			System.Math.Max(System.Math.Abs(r / g - 1), System.Math.Abs(b / g - 1)), 0.0, 1e-4);
	}

	/// <summary>
	/// Extinction must be positive everywhere. It is not guaranteed here: `ozoneAbsorption.x`
	/// is negative in the shipped asset, so a large enough `ozoneStrength` makes the red
	/// channel's extinction negative - which means an atmosphere that *creates* energy, a
	/// transmittance above 1, and a silently clamped LUT since it stores UNorm.
	///
	/// This scan is the check that turns that from a latent trap into a caught one.
	/// </summary>
	static int CheckExtinctionPositive(StringBuilder report, AtmosphereEffect a)
	{
		report.Append("\n## Extinction positivity\n\n");

		float worst = float.MaxValue;
		float worstHeight = 0;
		int channel = 0;

		for (int i = 0; i <= 10000; i++)
		{
			float h = a.atmosphereThickness * i / 10000f;
			Vector3 e = AtmosphereReference.Extinction(a, h);
			for (int c = 0; c < 3; c++)
			{
				if (e[c] < worst) { worst = e[c]; worstHeight = h; channel = c; }
			}
		}

		report.Append($"- minimum extinction {F(worst)} in {"RGB"[channel]} at altitude {F(worstHeight)}\n");

		// Also report how much headroom the ozone slider has, since that is the live hazard.
		float breakingStrength = FindOzoneStrengthThatBreaks(a);
		report.Append(breakingStrength > 0
			? $"- extinction turns negative at ozoneStrength >= {F(breakingStrength)} (currently {F(a.ozoneStrength)})\n"
			: "- extinction stays positive across the whole ozoneStrength range\n");

		return worst > 0 ? Pass(report, "extinction positive everywhere") : Fail(report, "extinction NEGATIVE - the atmosphere creates energy");
	}

	static float FindOzoneStrengthThatBreaks(AtmosphereEffect a)
	{
		float original = a.ozoneStrength;
		try
		{
			for (float s = 0.05f; s <= 5f; s += 0.05f)
			{
				a.ozoneStrength = s;
				for (int i = 0; i <= 400; i++)
				{
					Vector3 e = AtmosphereReference.Extinction(a, a.atmosphereThickness * i / 400f);
					if (e.x <= 0 || e.y <= 0 || e.z <= 0) { return s; }
				}
			}
			return -1;
		}
		finally { a.ozoneStrength = original; }
	}

	/// <summary>
	/// Vertical optical depth against the closed form. For an exponential profile the integral
	/// is exactly beta*H, and the ozone tent's area is exactly base*height/2 - so this needs no
	/// quadrature to check against.
	/// </summary>
	static int CheckOpticalDepth(StringBuilder report, AtmosphereEffect a)
	{
		report.Append("\n## Vertical optical depth\n\n");

		Vector3 numeric = AtmosphereReference.VerticalOpticalDepth(a, 200000);
		Vector3 beta = AtmosphereReference.RayleighCoefficients(a);

		// Closed form for the Rayleigh part alone, as an independent handle on the quadrature.
		float h = a.rayleighDensityAvg;
		float closedRayleigh = beta.z * h * (1f - Mathf.Exp(-1f / h));

		report.Append($"- total (R, G, B) = ({F(numeric.x)}, {F(numeric.y)}, {F(numeric.z)})\n")
			.Append("- Earth reference, Bruneton/Hillaire: (0.046, 0.109, 0.265)\n")
			.Append($"- ratio to Earth: ({F(numeric.x / 0.0464f)}, {F(numeric.y / 0.1085f)}, {F(numeric.z / 0.2648f)})\n");

		// The Rayleigh-only closed form must match the blue channel minus the other species.
		Vector3 rayleighOnly = AtmosphereReference.VerticalOpticalDepth(a, 200000, rayleighOnly: true);
		return Assert(report, "Rayleigh vertical optical depth matches beta*H closed form",
			rayleighOnly.z, closedRayleigh, 1e-4);
	}

	/// <summary>
	/// At whitePoint = 1 the extended Reinhard is an exact identity, so the tone map is affine
	/// with a soft floor and its usable input band is narrow. Every change to absolute energy
	/// has to land inside that band, so it is worth printing rather than rediscovering.
	/// </summary>
	static int CheckToneMapBand(StringBuilder report, AtmosphereEffect a)
	{
		report.Append("\n## Tone map\n\n");

		float black = AtmosphereReference.InputForDisplay(a, 0f);
		float white = AtmosphereReference.InputForDisplay(a, 1f);

		report.Append($"- intensity {F(a.intensity)}, contrast {F(a.contrast)}, whitePoint {F(a.whitePoint)}\n")
			.Append($"- usable input band [{F(black)}, {F(white)}] = a factor of {F(white / black)}\n");

		if (Mathf.Approximately(a.whitePoint, 1f))
		{
			report.Append("- NOTE: at whitePoint = 1 the extended Reinhard is an exact identity " +
				"(v(1+v)/(1+v) = v), so there is no highlight rolloff at all.\n");
		}

		return black > 0 && white > black
			? Pass(report, "tone map band is well formed")
			: Fail(report, "tone map band is degenerate");
	}

	static int CheckSunDisc(StringBuilder report, AtmosphereEffect a)
	{
		report.Append("\n## Sun disc\n\n");

		const float realAngularRadius = 0.2667f;
		float ratio = a.sunDiscSize / realAngularRadius;

		report.Append($"- configured {F(a.sunDiscSize)} deg against the sun's {realAngularRadius} deg\n")
			.Append($"- {F(ratio)}x in angle, {F(ratio * ratio)}x in solid angle\n");

		return Mathf.Abs(ratio - 1f) < 0.05f
			? Pass(report, "sun disc matches the real angular radius")
			: Warn(report, "sun disc is not the real angular radius");
	}

	// ------------------------------------------------------------------ GPU readback

	/// <summary>
	/// The end-to-end check. Reads the shader's own transmittance LUT and compares the zenith
	/// texel against a closed form.
	///
	/// It is compared against *two* references, and the gap between them is the point. The
	/// closed form is the true optical depth; the right-Riemann sum is what
	/// `getSunTransmittance` actually computes, because it advances the sample position before
	/// sampling rather than at the midpoint. If the LUT matches the Riemann value the shader
	/// implements what this file's mirror says it does, and the distance to the closed form is
	/// the sampling bias waiting to be fixed.
	/// </summary>
	static int CheckTransmittanceLUT(StringBuilder report, AtmosphereEffect a)
	{
		report.Append("\n## Transmittance LUT (GPU readback)\n\n");

		if (a.transmittanceLUT == null)
		{
			return Warn(report, "transmittance LUT not allocated - open the scene and try again");
		}

		Color zenith = ReadTexel(a.transmittanceLUT, 0, 0);

		Vector3 tau = AtmosphereReference.VerticalOpticalDepth(a, 200000);
		var exact = new Vector3(Mathf.Exp(-tau.x), Mathf.Exp(-tau.y), Mathf.Exp(-tau.z));

		Vector3 tauRiemann = AtmosphereReference.VerticalOpticalDepthRightRiemann(a, 40);
		var riemann = new Vector3(Mathf.Exp(-tauRiemann.x), Mathf.Exp(-tauRiemann.y), Mathf.Exp(-tauRiemann.z));

		report.Append($"- LUT texel (0,0)  = ({F(zenith.r)}, {F(zenith.g)}, {F(zenith.b)})\n")
			.Append($"- 40-step right-Riemann = ({F(riemann.x)}, {F(riemann.y)}, {F(riemann.z)})  <- what the shader computes\n")
			.Append($"- exact closed form     = ({F(exact.x)}, {F(exact.y)}, {F(exact.z)})\n")
			.Append($"- sampling bias: optical depth low by {F(100f * (1f - tauRiemann.z / tau.z))}% in blue\n");

		int failures = Assert(report, "LUT zenith matches the shader's own quadrature",
			Distance(zenith, riemann), 0.0, 2e-3);

		// Range check across the whole texture: transmittance is a fraction and cannot exceed 1.
		Color[] all = ReadAll(a.transmittanceLUT);
		float maxValue = 0f, minValue = 1f;
		foreach (Color c in all)
		{
			maxValue = Mathf.Max(maxValue, Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
			minValue = Mathf.Min(minValue, Mathf.Min(c.r, Mathf.Min(c.g, c.b)));
		}
		report.Append($"- LUT range [{F(minValue)}, {F(maxValue)}]\n");

		failures += maxValue <= 1.0001f
			? Pass(report, "transmittance never exceeds 1")
			: Fail(report, "transmittance EXCEEDS 1 - negative extinction, silently clamped by the UNorm format");

		return failures;
	}

	static Color ReadTexel(RenderTexture source, int x, int y)
	{
		var texture = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, linear: true);
		RenderTexture previous = RenderTexture.active;
		RenderTexture.active = source;
		texture.ReadPixels(new Rect(x, y, 1, 1), 0, 0);
		texture.Apply();
		RenderTexture.active = previous;

		Color c = texture.GetPixel(0, 0);
		Object.DestroyImmediate(texture);
		return c;
	}

	static Color[] ReadAll(RenderTexture source)
	{
		var texture = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false, linear: true);
		RenderTexture previous = RenderTexture.active;
		RenderTexture.active = source;
		texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
		texture.Apply();
		RenderTexture.active = previous;

		Color[] pixels = texture.GetPixels();
		Object.DestroyImmediate(texture);
		return pixels;
	}

	// ------------------------------------------------------------------ helpers

	/// <summary>Gauss-free but ample: the phase functions are smooth and this only has to
	/// resolve the Mie forward peak, which 200k samples does comfortably.</summary>
	static double IntegrateOverSphere(System.Func<float, double> phase)
	{
		const int n = 200000;
		double total = 0;
		for (int i = 0; i < n; i++)
		{
			double mu = -1.0 + 2.0 * (i + 0.5) / n;
			total += phase((float)mu);
		}
		return total * (2.0 / n) * 2.0 * System.Math.PI;
	}

	static double Distance(Color a, Vector3 b)
	{
		return System.Math.Max(System.Math.Abs(a.r - b.x),
			System.Math.Max(System.Math.Abs(a.g - b.y), System.Math.Abs(a.b - b.z)));
	}

	static int Assert(StringBuilder report, string what, double actual, double expected, double tolerance)
	{
		double error = System.Math.Abs(actual - expected);
		bool ok = error <= tolerance;
		report.Append(ok ? "- PASS " : "- **FAIL** ")
			.Append(what)
			.Append($" (got {actual.ToString("G8", Ci)}, want {expected.ToString("G8", Ci)}, error {error.ToString("G3", Ci)})\n");
		return ok ? 0 : 1;
	}

	static int Pass(StringBuilder report, string what) { report.Append("- PASS ").Append(what).Append('\n'); return 0; }
	static int Fail(StringBuilder report, string what) { report.Append("- **FAIL** ").Append(what).Append('\n'); return 1; }
	static int Warn(StringBuilder report, string what) { report.Append("- warn: ").Append(what).Append('\n'); return 0; }

	static string F(float v) => v.ToString("F4", Ci);

	static AtmosphereEffect FindAtmosphere()
	{
		foreach (string guid in AssetDatabase.FindAssets("t:AtmosphereEffect"))
		{
			var effect = AssetDatabase.LoadAssetAtPath<AtmosphereEffect>(AssetDatabase.GUIDToAssetPath(guid));
			if (effect != null) { return effect; }
		}
		return null;
	}
}
