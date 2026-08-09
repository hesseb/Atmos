using System;
using System.Globalization;
using System.Text;

/// <summary>
/// Frame-time statistics.
///
/// Every definition here is chosen to be stated unambiguously in the thesis, because the
/// competing conventions differ by enough to change a conclusion:
///
///   - Percentiles use Hyndman-Fan type 7 (linear interpolation between order
///     statistics), which is what numpy.percentile and Excel PERCENTILE.INC produce.
///   - The 1% low is the MEAN of the slowest 1% of frames, not the 1st percentile. See
///     <see cref="Summary.p1LowMeanMs"/>.
///   - Average FPS is 1000/mean_ms, not the mean of per-frame FPS values. Those are
///     different quantities (harmonic vs arithmetic) and confusing them is a classic
///     benchmarking error.
/// </summary>
public static class BenchmarkStats
{
	public struct Summary
	{
		public int n;
		public double meanMs, medianMs, p95Ms, p99Ms, minMs, maxMs, stdDevMs, cv;

		/// <summary>
		/// Mean of the slowest 1% of frames, in ms. Reported in preference to the 1st
		/// percentile because it measures how bad the tail gets rather than only where it
		/// begins - and for a physically based atmosphere the worst frames are the
		/// interesting ones. NaN when n is too small for the average to mean anything.
		/// </summary>
		public double p1LowMeanMs;
		public double p01LowMeanMs;

		public double avgFps;
		/// <summary>Mean absolute frame-to-frame change. Closer to perceived smoothness
		/// than the mean is.</summary>
		public double jitterMs;
		public int framesOver16_67, framesOver33_33;

		public bool Valid => n > 0;
	}

	// Below these the "slowest 1%" is too few frames to average meaningfully.
	public const int MinSamplesFor1PercentLow = 300;
	public const int MinSamplesFor01PercentLow = 1000;

	/// <summary>
	/// <paramref name="sequence"/> must be in frame order - jitter depends on it.
	/// </summary>
	public static Summary Compute(double[] sequence, int count)
	{
		var summary = new Summary { n = count };
		if (count <= 0) { return summary; }

		var sorted = new double[count];
		Array.Copy(sequence, sorted, count);
		Array.Sort(sorted);

		double sum = 0;
		for (int i = 0; i < count; i++) { sum += sorted[i]; }

		summary.meanMs = sum / count;
		summary.minMs = sorted[0];
		summary.maxMs = sorted[count - 1];
		summary.medianMs = Percentile(sorted, 0.50);
		summary.p95Ms = Percentile(sorted, 0.95);
		summary.p99Ms = Percentile(sorted, 0.99);

		double variance = 0;
		for (int i = 0; i < count; i++)
		{
			double d = sorted[i] - summary.meanMs;
			variance += d * d;
		}
		// Sample standard deviation (n-1).
		summary.stdDevMs = count > 1 ? Math.Sqrt(variance / (count - 1)) : 0;
		summary.cv = summary.meanMs > 0 ? summary.stdDevMs / summary.meanMs : 0;

		summary.p1LowMeanMs = count >= MinSamplesFor1PercentLow
			? MeanOfSlowest(sorted, 0.01) : double.NaN;
		summary.p01LowMeanMs = count >= MinSamplesFor01PercentLow
			? MeanOfSlowest(sorted, 0.001) : double.NaN;

		summary.avgFps = summary.meanMs > 0 ? 1000.0 / summary.meanMs : 0;

		double jitterSum = 0;
		for (int i = 1; i < count; i++) { jitterSum += Math.Abs(sequence[i] - sequence[i - 1]); }
		summary.jitterMs = count > 1 ? jitterSum / (count - 1) : 0;

		for (int i = 0; i < count; i++)
		{
			if (sorted[i] > 16.6667) { summary.framesOver16_67++; }
			if (sorted[i] > 33.3333) { summary.framesOver33_33++; }
		}

		return summary;
	}

	/// <summary>Hyndman-Fan type 7. <paramref name="sorted"/> must be ascending.</summary>
	public static double Percentile(double[] sorted, double q)
	{
		int n = sorted.Length;
		if (n == 0) { return double.NaN; }
		if (n == 1) { return sorted[0]; }

		double h = (n - 1) * Math.Max(0.0, Math.Min(1.0, q));
		int lo = (int)Math.Floor(h);
		int hi = Math.Min(lo + 1, n - 1);
		return sorted[lo] + (h - lo) * (sorted[hi] - sorted[lo]);
	}

	/// <summary>Mean of the slowest fraction. Sorted ascending, so the slowest are last.</summary>
	static double MeanOfSlowest(double[] sorted, double fraction)
	{
		int k = Math.Max(1, (int)Math.Ceiling(sorted.Length * fraction));
		double sum = 0;
		for (int i = sorted.Length - k; i < sorted.Length; i++) { sum += sorted[i]; }
		return sum / k;
	}

	// ------------------------------------------------------------------ formatting

	public static string CsvHeader(string prefix)
	{
		return $"{prefix}_n,{prefix}_mean_ms,{prefix}_median_ms,{prefix}_p95_ms,{prefix}_p99_ms," +
			$"{prefix}_p1_low_mean_ms,{prefix}_p01_low_mean_ms,{prefix}_min_ms,{prefix}_max_ms," +
			$"{prefix}_stddev_ms,{prefix}_cv,{prefix}_avg_fps,{prefix}_jitter_ms," +
			$"{prefix}_frames_over_16_67,{prefix}_frames_over_33_33";
	}

	public static string ToCsv(Summary s)
	{
		var ci = CultureInfo.InvariantCulture;
		var sb = new StringBuilder();
		sb.Append(s.n.ToString(ci)).Append(',');
		sb.Append(N(s.meanMs)).Append(',');
		sb.Append(N(s.medianMs)).Append(',');
		sb.Append(N(s.p95Ms)).Append(',');
		sb.Append(N(s.p99Ms)).Append(',');
		sb.Append(N(s.p1LowMeanMs)).Append(',');
		sb.Append(N(s.p01LowMeanMs)).Append(',');
		sb.Append(N(s.minMs)).Append(',');
		sb.Append(N(s.maxMs)).Append(',');
		sb.Append(N(s.stdDevMs)).Append(',');
		sb.Append(N(s.cv)).Append(',');
		sb.Append(N(s.avgFps)).Append(',');
		sb.Append(N(s.jitterMs)).Append(',');
		sb.Append(s.framesOver16_67.ToString(ci)).Append(',');
		sb.Append(s.framesOver33_33.ToString(ci));
		return sb.ToString();
	}

	/// <summary>
	/// Invariant culture, always. This machine's locale is sv-SE, where the default
	/// ToString would emit "1,5" and silently corrupt every CSV column. NaN is written as
	/// an empty cell so a reader cannot mistake "not enough samples" for a value.
	/// </summary>
	public static string N(double value)
	{
		return double.IsNaN(value) ? "" : value.ToString("G9", CultureInfo.InvariantCulture);
	}

	// ------------------------------------------------------------------- self-test

	/// <summary>
	/// Checks the statistics against hand-computed values. Not an NUnit test: an asmdef
	/// test assembly cannot reference the predefined Assembly-CSharp where this lives.
	/// </summary>
	public static string SelfTest()
	{
		var sb = new StringBuilder("[BenchmarkStats] self-test\n");
		int failures = 0;

		void Check(string label, double actual, double expected, double tolerance = 1e-9)
		{
			bool ok = Math.Abs(actual - expected) <= tolerance
				|| (double.IsNaN(actual) && double.IsNaN(expected));
			if (!ok) { failures++; }
			sb.Append(ok ? "  ok   " : "  FAIL ").Append(label)
			  .Append("  got ").Append(N(actual)).Append("  want ").Append(N(expected)).Append('\n');
		}

		// Percentiles, Hyndman-Fan type 7, on [1,2,3,4,5].
		double[] simple = { 1, 2, 3, 4, 5 };
		Check("p50 of 1..5", Percentile(simple, 0.50), 3.0);
		Check("p25 of 1..5", Percentile(simple, 0.25), 2.0);
		Check("p95 of 1..5", Percentile(simple, 0.95), 4.8);
		Check("p0 of 1..5", Percentile(simple, 0.0), 1.0);
		Check("p100 of 1..5", Percentile(simple, 1.0), 5.0);

		// Mean, stddev, jitter, avg fps on a known sequence.
		double[] seq = { 10, 20, 10, 20 };
		Summary s = Compute(seq, seq.Length);
		Check("mean", s.meanMs, 15.0);
		Check("median", s.medianMs, 15.0);
		Check("min", s.minMs, 10.0);
		Check("max", s.maxMs, 20.0);
		// Sample stddev of {10,20,10,20}: variance = 4*25/3 = 33.333..
		Check("stddev (n-1)", s.stdDevMs, Math.Sqrt(100.0 / 3.0), 1e-9);
		// avg fps must be 1000/mean, NOT the mean of per-frame fps (which would be 75).
		Check("avg fps = 1000/mean", s.avgFps, 1000.0 / 15.0, 1e-9);
		// Jitter: |20-10| + |10-20| + |20-10| = 30, over 3 intervals.
		Check("jitter", s.jitterMs, 10.0);
		Check("frames over 16.67", s.framesOver16_67, 2);

		// 1% low is suppressed below the sample threshold rather than averaging a handful.
		Check("p1 low suppressed at n=4", s.p1LowMeanMs, double.NaN);

		// With enough samples: 1000 frames, the slowest 10 are 100ms, rest 10ms.
		var big = new double[1000];
		for (int i = 0; i < 1000; i++) { big[i] = i < 990 ? 10.0 : 100.0; }
		Summary bs = Compute(big, big.Length);
		// Slowest 1% = ceil(1000*0.01) = 10 frames, all 100ms.
		Check("p1 low mean = slowest 10", bs.p1LowMeanMs, 100.0);
		// Slowest 0.1% = ceil(1000*0.001) = 1 frame.
		Check("p01 low mean", bs.p01LowMeanMs, 100.0);
		// p99 by type 7 lands inside the 10ms run, so it differs from the 1% low - which
		// is exactly why both conventions are emitted.
		Check("p99 differs from p1 low", bs.p99Ms, 10.0 + 0.01 * 90.0, 1.0);

		// Locale: this machine is sv-SE, where the default ToString gives "1,5".
		string formatted = N(1.5);
		bool localeOk = formatted == "1.5";
		if (!localeOk) { failures++; }
		sb.Append(localeOk ? "  ok   " : "  FAIL ")
		  .Append("invariant culture  got \"").Append(formatted).Append("\"  want \"1.5\"\n");

		sb.Append(failures == 0 ? "  ALL PASS" : $"  {failures} FAILURE(S)");
		return sb.ToString();
	}
}
