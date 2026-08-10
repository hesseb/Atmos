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

		failures += CheckPhaseNormalisation(report, atmosphere);
		failures += CheckWavelengthLaw(report, atmosphere);
		failures += CheckExtinctionPositive(report, atmosphere);
		failures += CheckOpticalDepth(report, atmosphere);
		failures += CheckTransmittanceMapping(report, atmosphere);
		failures += CheckMarchQuadrature(report, atmosphere);
		failures += CheckToneMapBand(report, atmosphere);
		failures += CheckIlluminanceBookkeeping(report, atmosphere);
		failures += CheckSunDisc(report, atmosphere);
		failures += CheckTransmittanceLUT(report, atmosphere);
		failures += CheckMultipleScatteringLUT(report, atmosphere);

		report.Append(failures == 0
			? "\n**All checks passed.**\n"
			: $"\n**{failures} check(s) FAILED.**\n");

		if (failures == 0) { Debug.Log(report.ToString()); }
		else { Debug.LogError(report.ToString()); }
	}

	/// <summary>
	/// Kilometres per world unit, fixed by declaring the atmosphere column to be Earth's 100 km.
	///
	/// The planet cannot also be Earth-sized - the terrain globe is pinned at radius 150 - so
	/// this maps the *vertical* structure and leaves curvature wrong. That is the honest trade:
	/// every scale height, ozone altitude and optical depth becomes directly comparable to a
	/// published value, while the horizon still dips far too steeply for the altitude.
	/// </summary>
	static float KilometresPerUnit(AtmosphereEffect a) => 100f / a.atmosphereThickness;

	static void AppendParameters(StringBuilder report, AtmosphereEffect a)
	{
		float km = KilometresPerUnit(a);

		report.Append("| parameter | world units | kilometres |\n|---|---|---|\n")
			.Append($"| planet radius | {F(a.bodyRadius)} | {F(a.bodyRadius * km)} |\n")
			.Append($"| atmosphere thickness | {F(a.atmosphereThickness)} | {F(a.atmosphereThickness * km)} |\n")
			.Append($"| Rayleigh scale height | {F(a.rayleighDensityAvg * a.atmosphereThickness)} | " +
				$"{F(a.rayleighDensityAvg * a.atmosphereThickness * km)} (Earth 8.0) |\n")
			.Append($"| Mie scale height | {F(a.mieDensityAvg * a.atmosphereThickness)} | " +
				$"{F(a.mieDensityAvg * a.atmosphereThickness * km)} (Earth 1.2) |\n")
			.Append($"| ozone peak altitude | {F(a.ozonePeakDensityAltitude * a.atmosphereThickness)} | " +
				$"{F(a.ozonePeakDensityAltitude * a.atmosphereThickness * km)} (Earth 25.0) |\n")
			.Append($"| ozone half-width | {F(a.atmosphereThickness / a.ozoneDensityFalloff)} | " +
				$"{F(a.atmosphereThickness / a.ozoneDensityFalloff * km)} (Earth 15.0) |\n\n")
			.Append($"- 1 world unit = {F(km)} km, fixed by declaring the column to be Earth's 100 km\n")
			.Append($"- scale height ratio {F(a.rayleighDensityAvg / a.mieDensityAvg)} : 1 (Earth 6.67 : 1)\n")
			.Append($"- thickness / radius {F(a.atmosphereThickness / a.bodyRadius)} against Earth's 0.0157, " +
				"so **curvature is not to scale** even though the vertical structure now is\n")
			.Append($"- sky steps {a.numSkyScatteringSteps}; aerial {a.aerialStepsPerSlice} per slice " +
				$"x {a.aerialPerspectiveLUTSize} slices = {a.aerialStepsPerSlice * a.aerialPerspectiveLUTSize} total; " +
				$"a sky ray from the ground to the horizon is ~212 units, i.e. " +
				$"{F(212f / a.numSkyScatteringSteps)} units per step against a Mie scale height of " +
				$"{F(a.mieDensityAvg * a.atmosphereThickness)}\n\n");
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
	static int CheckPhaseNormalisation(StringBuilder report, AtmosphereEffect a)
	{
		report.Append("## Phase normalisation\n\n");

		double rayleigh = IntegrateOverSphere(mu => AtmosphereReference.RayleighPhase(mu));
		// The configured g, not a hardcoded 0.8 - the shader's asymmetry is authorable now, and a
		// harness that checks a different value than the shader uses is checking nothing.
		double mie = IntegrateOverSphere(mu => AtmosphereReference.MiePhase(mu, a.mieAsymmetry));

		double worstReduction = 0;
		for (int i = 0; i <= 200; i++)
		{
			double mu = -1.0 + 2.0 * i / 200.0;
			worstReduction = System.Math.Max(worstReduction,
				System.Math.Abs(AtmosphereReference.MiePhase((float)mu, 0f) - AtmosphereReference.RayleighPhase((float)mu)));
		}

		int failures = 0;
		failures += Assert(report, "Rayleigh integrates to 1", rayleigh, 1.0, 1e-6);
		failures += Assert(report, $"Cornette-Shanks (g={F(a.mieAsymmetry)}) integrates to 1", mie, 1.0, 1e-4);
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

		// Scientific notation: the value is tiny and "F4" renders it as a useless 0.0000.
		// Units are per world unit now, so this is 1/thickness of what it was before the
		// density model moved to absolute altitude - the physics is unchanged.
		report.Append($"- minimum extinction {E(worst)} per world unit, in {"RGB"[channel]} " +
			$"at altitude {F(worstHeight)}\n");

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
	/// The transmittance LUT's mapping must round-trip.
	///
	/// This is the whole risk of a reparameterisation: the compute writes through the inverse and
	/// every reader goes through the forward map, so if they disagree the LUT is filled correctly
	/// and read from the wrong place. The result is a plausible sky, not a broken one, which is
	/// why it needs an assertion rather than an eyeball.
	///
	/// Also checks the property that motivates Bruneton's mapping at all: u = 1 is the horizon,
	/// so the entire texture width is spent on rays that miss the ground.
	/// </summary>
	static int CheckTransmittanceMapping(StringBuilder report, AtmosphereEffect a)
	{
		report.Append("\n## Transmittance LUT mapping\n\n");

		float worstRadius = 0f, worstCosine = 0f;

		for (int i = 0; i <= 32; i++)
		{
			for (int j = 0; j <= 32; j++)
			{
				// Through texel centres, which is the domain the mapping is defined on.
				var uv = new Vector2(
					(i / 32f) * (1f - 1f / a.transmittanceLUTSize.x) + 0.5f / a.transmittanceLUTSize.x,
					(j / 32f) * (1f - 1f / a.transmittanceLUTSize.y) + 0.5f / a.transmittanceLUTSize.y);

				AtmosphereReference.TransmittanceLutParams(a, uv, out float radius, out float cosZenith);
				Vector2 back = AtmosphereReference.TransmittanceLutUv(a, radius, cosZenith);

				worstRadius = Mathf.Max(worstRadius, Mathf.Abs(back.y - uv.y));
				worstCosine = Mathf.Max(worstCosine, Mathf.Abs(back.x - uv.x));
			}
		}

		// At u = 1 the ray grazes the horizon by construction. Checking it at both ends of the
		// altitude range is what confirms no ground-intersecting ray is representable.
		float rt = a.bodyRadius + a.atmosphereThickness;
		float groundErr, topErr;
		{
			AtmosphereReference.TransmittanceLutParams(a, new Vector2(1f - 0.5f / a.transmittanceLUTSize.x, 0.5f / a.transmittanceLUTSize.y),
				out float r0, out float mu0);
			groundErr = Mathf.Abs(mu0 - AtmosphereReference.HorizonCosine(a, r0));

			AtmosphereReference.TransmittanceLutParams(a, new Vector2(1f - 0.5f / a.transmittanceLUTSize.x, 1f - 0.5f / a.transmittanceLUTSize.y),
				out float r1, out float mu1);
			topErr = Mathf.Abs(mu1 - AtmosphereReference.HorizonCosine(a, r1));
		}

		report.Append($"- {a.transmittanceLUTSize.x}x{a.transmittanceLUTSize.y}, "
				+ $"Rt/Rg = {F(rt / a.bodyRadius)} against Earth's 1.0157\n")
			.Append("- fraction of the old linear-in-mu width spent on rays through the planet, "
				+ "which this mapping does not store at all:\n");

		foreach (float radius in new[] { a.bodyRadius, a.bodyRadius + a.atmosphereThickness * 0.5f, rt })
		{
			float horizon = AtmosphereReference.HorizonCosine(a, radius);
			report.Append($"    r = {F(radius)}: horizon cosine {F(horizon)}, "
				+ $"{F(100f * (horizon * 0.5f + 0.5f))}% wasted\n");
		}

		int failures = 0;
		failures += Assert(report, "mapping round-trips in the distance coordinate", worstCosine, 0.0, 1e-5);
		failures += Assert(report, "mapping round-trips in the altitude coordinate", worstRadius, 0.0, 1e-5);
		failures += Assert(report, "u = 1 is the horizon at ground level", groundErr, 0.0, 1e-4);
		failures += Assert(report, "u = 1 is the horizon at the top of the atmosphere", topErr, 0.0, 1e-4);
		return failures;
	}

	/// <summary>
	/// How well the view march's step count resolves each species.
	///
	/// `raymarch` sampled at the start of each segment - a left-Riemann sum, which overestimates
	/// a decaying profile, and the mirror of the right-Riemann bias `getSunTransmittance` had.
	/// Both are now midpoint. This reports what that was worth, at the configured step counts,
	/// so the step counts themselves are justified on the record rather than by assertion.
	///
	/// Measured down a vertical column, which is the mild case: a horizon path traverses far
	/// more of the dense lower atmosphere per unit of ray length, so the real error along one is
	/// larger than the figure printed here.
	/// </summary>
	static int CheckMarchQuadrature(StringBuilder report, AtmosphereEffect a)
	{
		report.Append("\n## View march quadrature\n\n");

		Vector3 exact = AtmosphereReference.VerticalOpticalDepth(a, 200000);
		double exactMie = AtmosphereReference.MieVerticalOpticalDepth(a, 200000);
		string worstLabel = "";
		float worstError = 0f;

		// Compared by step SIZE rather than step count, because the two marches no longer measure
		// the same thing. The sky march spans the atmosphere in numSkyScatteringSteps; the aerial
		// perspective now advances one slice at a time, so its step is a slice's depth divided by
		// the per-slice count, and its total step count says nothing about its resolution.
		float skyStep = a.atmosphereThickness / Mathf.Max(1, a.numSkyScatteringSteps);
		float aerialStep = a.bodyRadius / Mathf.Max(1, a.aerialPerspectiveLUTSize)
			/ Mathf.Max(1, a.aerialStepsPerSlice);

		foreach ((string label, float stepSize) in new[] { ("sky", skyStep), ("aerial", aerialStep) })
		{
			// The reference integrates a vertical column, so a step size is expressed back as the
			// number of steps that column would take at that size.
			int steps = Mathf.Max(1, Mathf.RoundToInt(a.atmosphereThickness / stepSize));

			Vector3 mid = AtmosphereReference.VerticalOpticalDepth(a, steps);
			Vector3 left = AtmosphereReference.VerticalOpticalDepthLeftRiemann(a, steps);

			float midErr = 100f * Mathf.Abs(1f - mid.z / exact.z);
			float leftErr = 100f * Mathf.Abs(1f - left.z / exact.z);

			// Per species, because the total is dominated by Rayleigh and hides the species that
			// actually fails. Reporting only the blue total let this check pass while the aerial
			// perspective was resolving Mie to within a factor of two - the exact shape of
			// misleading green tick this harness exists to prevent.
			double midMie = AtmosphereReference.MieVerticalOpticalDepth(a, steps);
			double leftMie = AtmosphereReference.MieVerticalOpticalDepth(a, steps, leftRiemann: true);
			float midMieErr = (float)(100.0 * System.Math.Abs(1.0 - midMie / exactMie));
			float leftMieErr = (float)(100.0 * System.Math.Abs(1.0 - leftMie / exactMie));

			report.Append($"- {label} march, {F(stepSize)} u per step:\n")
				.Append($"    blue total  midpoint {F(midErr)}%, left-Riemann {F(leftErr)}%\n")
				.Append($"    Mie alone   midpoint {F(midMieErr)}%, left-Riemann {F(leftMieErr)}%\n");

			if (midMieErr > worstError) { worstError = midMieErr; worstLabel = label; }
		}

		report.Append($"- Mie scale height {F(a.mieDensityAvg * a.atmosphereThickness)} u is the "
			+ "binding constraint, and it is only ~4% of blue extinction, so a total-blue "
			+ "figure understates how badly it is resolved\n");

		// A warning rather than a failure: the aerial perspective's step count is genuinely too
		// low for the Mie layer, and the fix is the incremental march that makes steps cheap,
		// not a number change here.
		return worstError < 5f
			? Pass(report, "both step counts resolve the Mie layer")
			: Warn(report, $"the {worstLabel} march leaves {F(worstError)}% error in Mie optical depth");
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

		if (black <= 0 || white <= black) { return Fail(report, "tone map band is degenerate"); }

		// Whether the curve has a shoulder at all, which is a separate question from whether
		// the band is well formed - and the one that actually bit.
		//
		// This was printed as a NOTE and treated as documentation. It stopped being cosmetic
		// the moment the Mie forward lobe was corrected: in-scatter at the sun rose 6.6x in
		// red against 3.3x in blue, and with no rolloff every channel pinned to 1 together, so
		// the extra red arrived as white. A shoulder is what lets red stay ahead of blue.
		if (Mathf.Approximately(a.whitePoint, 1f))
		{
			report.Append("- at whitePoint = 1 the extended Reinhard is an exact identity " +
				"(v(1+v)/(1+v) = v), so highlights clip rather than roll off\n");
			return Warn(report, "tone map has no highlight rolloff");
		}

		report.Append($"- whitePoint {F(a.whitePoint)} gives a shoulder, so highlights above "
			+ $"{F(white)} compress instead of clipping, and hue survives into them\n");
		return Pass(report, "tone map band is well formed and rolls off");
	}

	/// <summary>
	/// The Stage 2 bookkeeping, stated as an assertion rather than left in a comment.
	///
	/// The old code used `rayleighPhaseValue = 1`. A normalised phase averages 1/(4*PI), so what
	/// now stands in that slot is `sunIlluminance * <P_R>` - and at E = 4*PI that is exactly 1,
	/// leaving Rayleigh's sphere-average untouched while giving it angular structure it had none
	/// of before.
	///
	/// The same E multiplies Mie, which already had its phase, so Mie rises by E. That is the
	/// correction rather than a side effect: Rayleigh was over-weighted against Mie by that
	/// factor, which is why mieCoefficient had to be inflated to compete with it.
	/// </summary>
	static int CheckIlluminanceBookkeeping(StringBuilder report, AtmosphereEffect a)
	{
		report.Append("\n## Illuminance bookkeeping\n\n");

		double meanPhase = IntegrateOverSphere(mu => AtmosphereReference.RayleighPhase(mu)) / (4.0 * System.Math.PI);
		double effective = a.sunIlluminance * meanPhase;

		report.Append($"- mean Rayleigh phase over the sphere {F((float)meanPhase)}, i.e. 1/4pi\n")
			.Append($"- E x mean phase = {F((float)effective)}, against the 1 the hardcoded phase used\n")
			.Append($"- Mie keeps its own phase, so it gains E = {F(a.sunIlluminance)}x against Rayleigh\n");

		return Assert(report, "E x mean Rayleigh phase reproduces the old unit weight", effective, 1.0, 1e-4);
	}

	static int CheckSunDisc(StringBuilder report, AtmosphereEffect a)
	{
		report.Append("\n## Sun disc\n\n");

		const float realAngularRadius = 0.2667f;
		float ratio = a.sunDiscSize / realAngularRadius;

		// F(), not raw interpolation: this machine is sv-SE, where a bare float renders as
		// "0,2667" and the report picks up a decimal comma.
		report.Append($"- configured {F(a.sunDiscSize)} deg against the sun's {F(realAngularRadius)} deg\n")
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

		// The shader samples at the midpoint of each of 40 steps. It used to advance before
		// sampling, a right-Riemann sum; both are reported so the improvement is visible as a
		// number rather than asserted.
		Vector3 tauMidpoint = AtmosphereReference.VerticalOpticalDepth(a, 40);
		var midpoint = new Vector3(Mathf.Exp(-tauMidpoint.x), Mathf.Exp(-tauMidpoint.y), Mathf.Exp(-tauMidpoint.z));

		Vector3 tauRiemann = AtmosphereReference.VerticalOpticalDepthRightRiemann(a, 40);
		var riemann = new Vector3(Mathf.Exp(-tauRiemann.x), Mathf.Exp(-tauRiemann.y), Mathf.Exp(-tauRiemann.z));

		report.Append($"- LUT texel (0,0)   = ({F(zenith.r)}, {F(zenith.g)}, {F(zenith.b)})\n")
			.Append($"- 40-step midpoint  = ({F(midpoint.x)}, {F(midpoint.y)}, {F(midpoint.z)})  <- what the shader computes\n")
			.Append($"- exact closed form = ({F(exact.x)}, {F(exact.y)}, {F(exact.z)})\n")
			.Append($"- 40-step right-Riemann = ({F(riemann.x)}, {F(riemann.y)}, {F(riemann.z)})  <- what it computed before\n")
			.Append($"- quadrature error in blue: midpoint {F(100f * Mathf.Abs(1f - tauMidpoint.z / tau.z))}%, " +
				$"right-Riemann {F(100f * Mathf.Abs(1f - tauRiemann.z / tau.z))}%\n");

		int failures = Assert(report, "LUT zenith matches the shader's own quadrature",
			Distance(zenith, midpoint), 0.0, 2e-3);

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

	/// <summary>
	/// The multiple-scattering LUT, checked on the one property that decides whether the model
	/// is valid at all.
	///
	/// Psi_ms = L2 / (1 - f_ms) is a geometric series over scattering orders, and it converges
	/// only while f_ms &lt; 1. At f_ms >= 1 the atmosphere would be returning more light than
	/// falls on it, and the expression goes negative rather than infinite - so the failure would
	/// arrive as a dark or inverted sky, not as an obvious NaN.
	///
	/// The compute stores max(f_ms) in alpha precisely so this can be read back and asserted.
	/// </summary>
	static int CheckMultipleScatteringLUT(StringBuilder report, AtmosphereEffect a)
	{
		report.Append("\n## Multiple scattering LUT\n\n");

		if (a.multipleScatteringLUT == null)
		{
			return Warn(report, "multiple scattering LUT has not been built");
		}

		Color[] all = ReadAll(a.multipleScatteringLUT);
		float maxFms = 0f, maxPsi = 0f, minPsi = float.MaxValue;
		int negative = 0, nonFinite = 0;

		foreach (Color c in all)
		{
			maxFms = Mathf.Max(maxFms, c.a);
			float peak = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
			float trough = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
			maxPsi = Mathf.Max(maxPsi, peak);
			minPsi = Mathf.Min(minPsi, trough);

			if (trough < 0f) { negative++; }
			if (float.IsNaN(peak) || float.IsInfinity(peak)) { nonFinite++; }
		}

		report.Append($"- {a.multipleScatteringLUTSize.x}x{a.multipleScatteringLUTSize.y}, "
				+ $"ground albedo {F(a.groundAlbedo)}, strength {F(a.multipleScatteringStrength)}\n")
			.Append($"- max f_ms = {F(maxFms)}, so the series gains {F(1f / Mathf.Max(1e-4f, 1f - maxFms))}x "
				+ "over single scattering at its strongest\n")
			.Append($"- Psi_ms range [{F(minPsi)}, {F(maxPsi)}]\n");

		int failures = 0;
		if (maxFms >= 0.95f) { failures += Fail(report, $"f_ms reaches {F(maxFms)}; the order series is at or past divergence"); }
		else { failures += Pass(report, "f_ms stays below 1, so the order series converges"); }

		if (negative > 0) { failures += Fail(report, $"{negative} texels have a negative Psi_ms"); }
		if (nonFinite > 0) { failures += Fail(report, $"{nonFinite} texels are NaN or infinite"); }
		if (negative == 0 && nonFinite == 0) { failures += Pass(report, "Psi_ms is non-negative and finite everywhere"); }

		return failures;
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
	static string E(float v) => v.ToString("E3", Ci);

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
