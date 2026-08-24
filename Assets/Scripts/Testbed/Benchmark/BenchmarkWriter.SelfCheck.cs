using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// See BenchmarkWriter.cs for the shadowing note this alias exists for.
using IOPath = System.IO.Path;

public static partial class BenchmarkWriter
{
	// -------------------------------------------------------------- selfcheck.md

	/// <summary>One statistic's spread across repeats of a single profile and segment.</summary>
	struct Spread
	{
		public string profileId, label;
		public int repeats;
		public double min, max, mean;

		public double AbsoluteMs => max - min;
		/// <summary>Spread as a fraction of the mean. The comparable form - an 0.05 ms spread
		/// means something very different at 1 ms than at 20 ms.</summary>
		public double Relative => mean > 0 ? (max - min) / mean : double.NaN;
		public bool Valid => repeats >= 2 && !double.IsNaN(mean);
	}

	/// <summary>
	/// Reports how far repeats of the *same* configuration disagreed.
	///
	/// Two kinds of claim, kept deliberately separate. The hash checks are pass/fail: they
	/// assert the runs were actually comparable, and a failure invalidates everything else.
	/// The spread is *reported and not judged* - there is no threshold here that could be
	/// mistaken for a standard, because what counts as acceptable depends entirely on how
	/// large the effect being measured is.
	/// </summary>
	static string BuildSelfCheck(RunMetadata meta, List<PassResult> passes)
	{
		var sb = new StringBuilder();
		sb.Append("# Self-check `").Append(meta.run_id).Append("`\n\n")
		  .Append("Repeats of the same configuration, run back to back in one process. ")
		  .Append("This measures the **harness and the machine**, not the renderer.\n\n");

		if (meta.is_editor)
		{
			sb.Append("> **Editor run.** The noise floor measured here includes editor ")
			  .Append("overhead and is not the one to quote. Re-run in a standalone build ")
			  .Append("before using this number.\n\n");
		}

		// --- part 1: the pass/fail claims ---
		sb.Append("## Comparability\n\n");

		bool posesAgree = true;
		for (int i = 1; i < passes.Count; i++)
		{
			if (passes[i].poseHash != passes[0].poseHash) { posesAgree = false; break; }
		}

		GeometryAgreement geometry = CompareGeometry(passes);

		sb.Append("| check | assertion | result |\n|---|---|---|\n")
		  .Append("| pose hash | every pass rendered the same camera and sun sequence | ")
		  .Append(posesAgree ? "**PASS**" : "**FAIL**").Append(" |\n")
		  .Append("| geometry | repeats submitted the same geometry workload | ")
		  .Append(geometry.Verdict).Append(" |\n\n");

		if (!posesAgree)
		{
			sb.Append("> **Pose hash mismatch.** The passes did not render the same poses, so ")
			  .Append("nothing below is a comparison. Fix this first.\n\n");
		}

		if (geometry.compared > 0 && !geometry.countersPresent)
		{
			sb.Append("> **Geometry could not be checked.** The Render profiler counters read ")
			  .Append("zero on every frame, which means they were stripped - this is a release ")
			  .Append("player. Frame timings are unaffected and the spread below stands, but ")
			  .Append("nothing here can confirm the repeats drew the same thing. Use a ")
			  .Append("development build if that assurance matters.\n\n");
		}

		if (geometry.differing > 0)
		{
			sb.Append("Geometry differed on **").Append(geometry.differing.ToString(Ci))
			  .Append(" of ").Append(geometry.compared.ToString(Ci))
			  .Append(" measured frames** (").Append(Pct(geometry.Fraction)).Append(").");

			if (geometry.Isolated)
			{
				// A hitch inflates one frame's counters by absorbing several frames' work, so
				// the same profiler frame reports six times the geometry and an invalid GPU
				// time. Reporting that as "the repeats were not comparable" would be wrong -
				// it is one bad frame, and it is visible in the wall clock.
				sb.Append(" That is isolated rather than systematic, which is the signature ")
				  .Append("of a stall - the profiler folds several frames' counters into one ")
				  .Append("when the main thread blocks. ");

				if (geometry.worstWallMs > 0)
				{
					sb.Append("The slowest measured frame in this run took **")
					  .Append(M(geometry.worstWallMs)).Append(" ms** wall clock, against a ")
					  .Append("median of ").Append(M(geometry.medianWallMs)).Append(" ms. ");
				}

				sb.Append("Treat the spread below as usable, but confirm the affected frames ")
				  .Append("are not the ones a conclusion rests on.\n\n");
			}
			else
			{
				sb.Append(" That is systematic, not a stall: the repeats genuinely drew ")
				  .Append("different things, so the spread below is not a noise floor. ")
				  .Append("Investigate before using it.\n\n");
			}
		}

		// --- part 2: the number ---
		List<Spread> median = CollectSpread(passes, s => s.gpu.medianMs);
		List<Spread> p99 = CollectSpread(passes, s => s.gpu.p99Ms);
		List<Spread> p1Low = CollectSpread(passes, s => s.gpu.p1LowMeanMs);

		sb.Append("## Run-to-run spread, GPU frame time\n\n")
		  .Append("Max minus min across repeats, per profile and segment. Reported, not ")
		  .Append("judged: whether a spread is acceptable depends on the size of the effect ")
		  .Append("being measured.\n\n");

		sb.Append("| profile | segment | repeats | median (ms) | spread | spread % | p99 spread | 1% low spread |\n")
		  .Append("|---|---|---|---|---|---|---|---|\n");

		for (int i = 0; i < median.Count; i++)
		{
			Spread m = median[i];
			sb.Append("| ").Append(m.profileId)
			  .Append(" | ").Append(m.label)
			  .Append(" | ").Append(m.repeats.ToString(Ci))
			  .Append(" | ").Append(M(m.mean))
			  .Append(" | ").Append(M(m.AbsoluteMs))
			  .Append(" | ").Append(Pct(m.Relative))
			  .Append(" | ").Append(M(Find(p99, m).AbsoluteMs))
			  .Append(" | ").Append(M(Find(p1Low, m).AbsoluteMs))
			  .Append(" |\n");
		}

		Spread worst = Worst(median);

		sb.Append("\n## Noise floor\n\n");

		if (!worst.Valid)
		{
			sb.Append("Not computable - fewer than two repeats produced valid GPU timings.\n");
			return sb.ToString();
		}

		sb.Append("The largest run-to-run spread in **median GPU frame time** was ")
		  .Append("**").Append(M(worst.AbsoluteMs)).Append(" ms** (")
		  .Append(Pct(worst.Relative)).Append(" of the median), on `")
		  .Append(worst.profileId).Append(" / ").Append(worst.label).Append("`.\n\n")
		  .Append("Read this as: a difference between two renderer configurations smaller ")
		  .Append("than roughly this much cannot be distinguished from run-to-run variation ")
		  .Append("on this machine, and should be reported as such rather than as a result. ")
		  .Append("It is a single-machine, single-session figure - it does not generalise, ")
		  .Append("and it should be re-measured whenever the hardware, driver or scene ")
		  .Append("changes.\n\n")
		  .Append("Note that the p99 and 1% low columns are typically several times wider ")
		  .Append("than the median column. Tail statistics are inherently less stable, so a ")
		  .Append("tail difference needs a correspondingly larger margin before it means ")
		  .Append("anything.\n");

		return sb.ToString();
	}

	/// <summary>How far repeats of one profile agreed on what they drew, frame by frame.</summary>
	struct GeometryAgreement
	{
		public int compared, differing;
		public double worstWallMs, medianWallMs;
		/// <summary>False when the Render profiler counters were unavailable - a release
		/// player strips them. Without this, every frame reports zero geometry, every pass
		/// agrees trivially, and the check reports a confident PASS having compared
		/// nothing.</summary>
		public bool countersPresent;

		public double Fraction => compared > 0 ? (double)differing / compared : 0.0;

		/// <summary>
		/// A handful of disagreeing frames is a stall artefact; a steady stream is a real
		/// difference in what was rendered. The threshold is deliberately loose - it only
		/// decides the wording, and both cases are reported with their actual counts.
		/// </summary>
		public bool Isolated => differing > 0 && Fraction < 0.005;

		public string Verdict
		{
			get
			{
				if (compared == 0) { return "n/a"; }
				if (!countersPresent) { return "n/a (profiler counters unavailable)"; }
				if (differing == 0) { return "**PASS**"; }
				return Isolated ? "**PASS** (isolated stalls)" : "**FAIL**";
			}
		}
	}

	/// <summary>
	/// Compares measured-frame geometry between repeats of the same profile. Across profiles
	/// it is skipped deliberately: two renderers legitimately draw different things, and that
	/// difference is a result rather than a fault.
	/// </summary>
	static GeometryAgreement CompareGeometry(List<PassResult> passes)
	{
		var agreement = new GeometryAgreement { countersPresent = CountersPresent(passes) };
		var wall = new List<double>();

		foreach (PassResult pass in passes)
		{
			if (pass.geometry != null)
			{
				foreach (FrameGeometry g in pass.geometry)
				{
					wall.Add(g.wallMs);
					if (g.wallMs > agreement.worstWallMs) { agreement.worstWallMs = g.wallMs; }
				}
			}

			// Compare each pass against the first pass of its own profile.
			PassResult reference = null;
			foreach (PassResult candidate in passes)
			{
				if (candidate.profileId == pass.profileId) { reference = candidate; break; }
			}

			if (reference == null || ReferenceEquals(reference, pass)) { continue; }
			if (reference.geometry == null || pass.geometry == null) { continue; }

			int n = Mathf.Min(reference.geometry.Length, pass.geometry.Length);
			agreement.compared += n;

			for (int i = 0; i < n; i++)
			{
				FrameGeometry a = reference.geometry[i];
				FrameGeometry b = pass.geometry[i];

				if (a.drawCalls != b.drawCalls || a.triangles != b.triangles
					|| a.lodHighRes != b.lodHighRes)
				{
					agreement.differing++;
				}
			}
		}

		if (wall.Count > 0)
		{
			wall.Sort();
			agreement.medianWallMs = wall[wall.Count / 2];
		}

		return agreement;
	}

	static List<Spread> CollectSpread(List<PassResult> passes,
		System.Func<SegmentStats, double> select)
	{
		var result = new List<Spread>();
		var seen = new List<string>();

		foreach (PassResult pass in passes)
		{
			foreach (SegmentStats seg in pass.segments)
			{
				string key = pass.profileId + " :: " + seg.label;
				if (seen.Contains(key)) { continue; }
				seen.Add(key);

				var values = new List<double>();
				foreach (PassResult other in passes)
				{
					if (other.profileId != pass.profileId) { continue; }
					foreach (SegmentStats s in other.segments)
					{
						if (s.label != seg.label || !s.gpu.Valid) { continue; }
						double v = select(s);
						if (!double.IsNaN(v)) { values.Add(v); }
					}
				}

				if (values.Count < 2) { continue; }

				double min = values[0], max = values[0], sum = 0;
				foreach (double v in values)
				{
					if (v < min) { min = v; }
					if (v > max) { max = v; }
					sum += v;
				}

				result.Add(new Spread
				{
					profileId = pass.profileId,
					label = seg.label,
					repeats = values.Count,
					min = min,
					max = max,
					mean = sum / values.Count
				});
			}
		}

		return result;
	}

	static Spread Find(List<Spread> spreads, Spread like)
	{
		foreach (Spread s in spreads)
		{
			if (s.profileId == like.profileId && s.label == like.label) { return s; }
		}
		// NaN rather than zero: a missing statistic must print as "n/a", not as a spread of
		// 0.000 ms, which would read as perfect stability.
		return new Spread { mean = double.NaN, min = double.NaN, max = double.NaN };
	}

	static Spread Worst(List<Spread> spreads)
	{
		var worst = new Spread { mean = double.NaN };
		foreach (Spread s in spreads)
		{
			if (!worst.Valid || s.AbsoluteMs > worst.AbsoluteMs) { worst = s; }
		}
		return worst;
	}

	static string Pct(double fraction)
	{
		return double.IsNaN(fraction) ? "n/a" : (fraction * 100.0).ToString("F2", Ci) + "%";
	}

	/// <summary>
	/// Hash of the geometry workload: draw calls, triangles and LOD state over measured
	/// frames. Expected to be identical between repeats of one profile, and to DIFFER
	/// between profiles by exactly the passes each adds - which is itself a result worth
	/// reporting rather than an error.
	/// </summary>
	static ulong ComputeSceneHash(BenchmarkRunner runner)
	{
		ulong hash = 0xcbf29ce484222325UL;
		int limit = Mathf.Min(runner.FrameCursor, runner.Records.Length);

		for (int i = 0; i < limit; i++)
		{
			if (runner.Plan.frames[i].phase != BenchmarkPhase.Measure) { continue; }

			FrameSampler.Sample s = runner.Records[i].sample;
			Mix(ref hash, unchecked((ulong)s.drawCalls));
			Mix(ref hash, unchecked((ulong)s.triangles));
			Mix(ref hash, unchecked((ulong)runner.Records[i].lodHighResCount));
		}
		return hash;
	}

	static void Mix(ref ulong hash, ulong value)
	{
		for (int shift = 0; shift < 64; shift += 8)
		{
			hash ^= (value >> shift) & 0xFF;
			hash *= 0x100000001b3UL;
		}
	}

	static string Sanitise(string value)
	{
		if (string.IsNullOrEmpty(value)) { return "unnamed"; }
		foreach (char c in IOPath.GetInvalidFileNameChars()) { value = value.Replace(c, '-'); }
		return value.Replace(' ', '-');
	}

}
