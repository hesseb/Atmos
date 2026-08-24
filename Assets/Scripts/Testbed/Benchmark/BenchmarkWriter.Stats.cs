using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// See BenchmarkWriter.cs for the shadowing note this alias exists for.
using IOPath = System.IO.Path;

public static partial class BenchmarkWriter
{
	// ------------------------------------------------------------ segment stats

	public struct SegmentStats
	{
		public int index;
		public string label;
		public int frames;
		public BenchmarkStats.Summary gpu, cpu, wall;
		public double meanSkyFraction;
		public int meanLodHighRes;
	}

	static SegmentStats[] ComputeSegments(BenchmarkRunner runner)
	{
		BenchmarkPlan plan = runner.Plan;
		int limit = Mathf.Min(runner.FrameCursor, runner.Records.Length);
		var result = new List<SegmentStats>();

		// No statistics from a capture run. Its frame times are readback stalls; reporting a
		// median over them would produce a table that looks ordinary and is wrong.
		if (runner.IsCaptureRun) { return result.ToArray(); }

		for (int seg = 0; seg < plan.segmentLabels.Length; seg++)
		{
			var gpu = new List<double>();
			var cpu = new List<double>();
			var wall = new List<double>();
			double skySum = 0;
			long lodSum = 0;

			for (int i = 0; i < limit; i++)
			{
				PlannedFrame p = plan.frames[i];
				// Statistics use measured frames only. Boot, prewarm, warmup, settle and
				// flush are recorded in the CSV but excluded here, so the exclusion is
				// auditable rather than invisible.
				if (p.phase != BenchmarkPhase.Measure || p.segmentIndex != seg) { continue; }

				FrameSampler.Sample s = runner.Records[i].sample;
				if (s.timingValid)
				{
					gpu.Add(s.gpuMs);
					cpu.Add(s.cpuFrameMs);
				}
				wall.Add(s.wallMs);
				skySum += p.skyFraction;
				lodSum += runner.Records[i].lodHighResCount;
			}

			if (wall.Count == 0) { continue; }

			result.Add(new SegmentStats
			{
				index = seg,
				label = plan.segmentLabels[seg],
				frames = wall.Count,
				gpu = BenchmarkStats.Compute(gpu.ToArray(), gpu.Count),
				cpu = BenchmarkStats.Compute(cpu.ToArray(), cpu.Count),
				wall = BenchmarkStats.Compute(wall.ToArray(), wall.Count),
				meanSkyFraction = skySum / wall.Count,
				meanLodHighRes = (int)(lodSum / wall.Count)
			});
		}

		return result.ToArray();
	}

	static string BuildSegmentsCsv(BenchmarkRunner runner, SegmentStats[] segments)
	{
		var sb = new StringBuilder();
		sb.Append("segment_index,segment_label,frames,mean_sky_fraction,mean_lod_high_res,")
		  .Append(BenchmarkStats.CsvHeader("gpu")).Append(',')
		  .Append(BenchmarkStats.CsvHeader("cpu")).Append(',')
		  .Append(BenchmarkStats.CsvHeader("wall")).Append('\n');

		foreach (SegmentStats s in segments)
		{
			sb.Append(s.index.ToString(Ci)).Append(',')
			  .Append(s.label).Append(',')
			  .Append(s.frames.ToString(Ci)).Append(',')
			  .Append(F(s.meanSkyFraction)).Append(',')
			  .Append(s.meanLodHighRes.ToString(Ci)).Append(',')
			  .Append(BenchmarkStats.ToCsv(s.gpu)).Append(',')
			  .Append(BenchmarkStats.ToCsv(s.cpu)).Append(',')
			  .Append(BenchmarkStats.ToCsv(s.wall)).Append('\n');
		}

		return sb.ToString();
	}

}
