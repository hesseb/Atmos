using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// See BenchmarkWriter.cs for the shadowing note this alias exists for.
using IOPath = System.IO.Path;

public static partial class BenchmarkWriter
{
	// ---------------------------------------------------------------- batches

	/// <summary>One benchmark's completed run, as seen from a batch.</summary>
	public class BatchEntry
	{
		public string benchmarkId, runFolder;
		public BenchmarkRunMode mode;
		public List<PassResult> passes = new List<PassResult>();
	}

	/// <summary>
	/// Creates the folder a batch of runs writes into. Each run still gets its own subfolder,
	/// because they have different plans and different frame counts and so cannot share one -
	/// the batch only groups them and adds a view across them.
	/// </summary>
	public static string BeginBatch(string outputRoot)
	{
		GitInfo.TryGet(out string commit, out _, out _);

		string stamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss", Ci);
		string shortCommit = string.IsNullOrEmpty(commit) ? "nogit" : commit;
		string folder = IOPath.Combine(outputRoot, $"{stamp}_batch_{shortCommit}");

		Directory.CreateDirectory(folder);
		return folder;
	}

	/// <summary>
	/// Writes the cross-benchmark view: a markdown table per benchmark with each profile's
	/// median and its delta from the baseline, plus one flat CSV of every segment in the
	/// batch.
	///
	/// The CSV is the one that matters for the report - it is every segments.csv in the batch
	/// with benchmark, profile and repeat columns prepended, which is the shape a plotting
	/// script wants and the shape a per-run folder layout makes annoying to assemble.
	/// </summary>
	public static void WriteBatchSummary(string batchFolder, List<BatchEntry> entries,
		string machineLabel)
	{
		if (entries == null || entries.Count == 0) { return; }

		File.WriteAllText(IOPath.Combine(batchFolder, "batch-summary.csv"), BuildBatchCsv(entries));
		File.WriteAllText(IOPath.Combine(batchFolder, "batch-summary.md"),
			BuildBatchSummary(batchFolder, entries, machineLabel));
	}

	static string BuildBatchCsv(List<BatchEntry> entries)
	{
		var sb = new StringBuilder();
		sb.Append("benchmark,mode,pass_id,profile,repeat,segment_index,segment_label,frames,")
		  .Append("mean_sky_fraction,mean_lod_high_res,")
		  .Append(BenchmarkStats.CsvHeader("gpu")).Append(',')
		  .Append(BenchmarkStats.CsvHeader("cpu")).Append(',')
		  .Append(BenchmarkStats.CsvHeader("wall")).Append('\n');

		foreach (BatchEntry entry in entries)
		{
			foreach (PassResult pass in entry.passes)
			{
				if (pass.segments == null) { continue; }

				foreach (SegmentStats s in pass.segments)
				{
					sb.Append(entry.benchmarkId).Append(',')
					  .Append(entry.mode).Append(',')
					  .Append(pass.passId).Append(',')
					  .Append(pass.profileId).Append(',')
					  .Append(pass.repeat.ToString(Ci)).Append(',')
					  .Append(s.index.ToString(Ci)).Append(',')
					  .Append(s.label).Append(',')
					  .Append(s.frames.ToString(Ci)).Append(',')
					  .Append(F(s.meanSkyFraction)).Append(',')
					  .Append(s.meanLodHighRes.ToString(Ci)).Append(',')
					  .Append(BenchmarkStats.ToCsv(s.gpu)).Append(',')
					  .Append(BenchmarkStats.ToCsv(s.cpu)).Append(',')
					  .Append(BenchmarkStats.ToCsv(s.wall)).Append('\n');
				}
			}
		}

		return sb.ToString();
	}

	static string BuildBatchSummary(string batchFolder, List<BatchEntry> entries,
		string machineLabel)
	{
		var sb = new StringBuilder();
		sb.Append("# Benchmark batch `").Append(IOPath.GetFileName(batchFolder)).Append("`\n\n");

		if (Application.isEditor)
		{
			sb.Append("> **Editor run - not authoritative.** Use a standalone build for ")
			  .Append("anything that goes in the report.\n\n");
		}

		GitInfo.TryGet(out string commit, out string branch, out bool dirty);
		sb.Append("| | |\n|---|---|\n")
		  .Append("| benchmarks | ").Append(entries.Count.ToString(Ci)).Append(" |\n")
		  .Append("| machine | ").Append(string.IsNullOrEmpty(machineLabel) ? "-" : machineLabel).Append(" |\n")
		  .Append("| commit | `").Append(commit).Append(dirty ? " (dirty)" : "").Append("` on ")
		  .Append(branch).Append(" |\n")
		  .Append("| GPU | ").Append(SystemInfo.graphicsDeviceName).Append(" |\n\n");

		sb.Append("| benchmark | mode | run folder |\n|---|---|---|\n");
		foreach (BatchEntry entry in entries)
		{
			sb.Append("| ").Append(entry.benchmarkId)
			  .Append(" | ").Append(entry.mode)
			  .Append(" | `").Append(IOPath.GetFileName(entry.runFolder ?? "-")).Append("` |\n");
		}
		sb.Append('\n');

		// Profile order is taken from the first entry that has any, so the baseline column is
		// the same one every table compares against.
		List<string> profiles = ProfileOrder(entries);

		if (profiles.Count == 0)
		{
			sb.Append("No statistics in this batch");
			sb.Append(entries[0].mode == BenchmarkRunMode.Capture
				? " - a capture run reports none by design.\n"
				: ".\n");
			return sb.ToString();
		}

		string baseline = profiles[0];

		sb.Append("## GPU frame time by benchmark (median of repeats, ms)\n\n")
		  .Append("Baseline is `").Append(baseline).Append("`. Deltas are ")
		  .Append("profile minus baseline, so positive means the profile cost more.\n\n");

		sb.Append("| benchmark | segment | sky frac |");
		foreach (string p in profiles) { sb.Append(' ').Append(p).Append(" |"); }
		for (int i = 1; i < profiles.Count; i++)
		{
			// ASCII only: the rest of this codebase is, and a stray multi-byte literal in a
			// source file that may be read as ANSI turns into mojibake in the report.
			sb.Append(" delta ").Append(profiles[i]).Append(" |");
		}
		sb.Append("\n|---|---|---|");
		for (int i = 0; i < profiles.Count + profiles.Count - 1; i++) { sb.Append("---|"); }
		sb.Append('\n');

		foreach (BatchEntry entry in entries)
		{
			foreach (string label in SegmentOrder(entry))
			{
				sb.Append("| ").Append(entry.benchmarkId)
				  .Append(" | ").Append(label)
				  .Append(" | ").Append(MeanSkyFraction(entry, label).ToString("F2", Ci)).Append(" |");

				var medians = new List<double>();
				foreach (string profile in profiles)
				{
					double m = MedianOfRepeats(entry.passes, profile, label);
					medians.Add(m);
					sb.Append(' ').Append(M(m)).Append(" |");
				}

				for (int i = 1; i < medians.Count; i++)
				{
					double delta = medians[i] - medians[0];
					string sign = double.IsNaN(delta) || delta < 0 ? "" : "+";
					sb.Append(' ').Append(sign).Append(M(delta)).Append(" |");
				}
				sb.Append('\n');
			}
		}

		sb.Append("\nEvery segment of every benchmark is in `batch-summary.csv`, with the ")
		  .Append("full statistic set per pass rather than only the median.\n");

		AppendBatchReproducibility(sb, entries);
		return sb.ToString();
	}

	static void AppendBatchReproducibility(StringBuilder sb, List<BatchEntry> entries)
	{
		sb.Append("\n## Reproducibility\n\n")
		  .Append("Pose hashes are comparable **within** a benchmark, not across them - ")
		  .Append("different benchmarks render different poses by design.\n\n")
		  .Append("| benchmark | passes agree | pose hash |\n|---|---|---|\n");

		foreach (BatchEntry entry in entries)
		{
			bool agree = true;
			for (int i = 1; i < entry.passes.Count; i++)
			{
				if (entry.passes[i].poseHash != entry.passes[0].poseHash) { agree = false; break; }
			}

			sb.Append("| ").Append(entry.benchmarkId)
			  .Append(" | ").Append(entry.passes.Count == 0 ? "n/a" : agree ? "**yes**" : "**NO**")
			  .Append(" | `0x").Append(entry.passes.Count > 0
				  ? entry.passes[0].poseHash.ToString("x16") : "-").Append("` |\n");
		}
	}

	static List<string> ProfileOrder(List<BatchEntry> entries)
	{
		var profiles = new List<string>();
		foreach (BatchEntry entry in entries)
		{
			foreach (PassResult pass in entry.passes)
			{
				if (pass.segments == null || pass.segments.Length == 0) { continue; }
				if (!string.IsNullOrEmpty(pass.profileId) && !profiles.Contains(pass.profileId))
				{
					profiles.Add(pass.profileId);
				}
			}
		}
		return profiles;
	}

	static List<string> SegmentOrder(BatchEntry entry)
	{
		var labels = new List<string>();
		foreach (PassResult pass in entry.passes)
		{
			if (pass.segments == null) { continue; }
			foreach (SegmentStats s in pass.segments)
			{
				if (!labels.Contains(s.label)) { labels.Add(s.label); }
			}
		}
		return labels;
	}

	static double MeanSkyFraction(BatchEntry entry, string label)
	{
		double sum = 0;
		int count = 0;

		foreach (PassResult pass in entry.passes)
		{
			if (pass.segments == null) { continue; }
			foreach (SegmentStats s in pass.segments)
			{
				if (s.label != label) { continue; }
				sum += s.meanSkyFraction;
				count++;
			}
		}

		return count > 0 ? sum / count : double.NaN;
	}

	/// <summary>Writes one pass's per-frame data and returns its statistics.</summary>
	public static PassResult WritePass(BenchmarkRunner runner, string runFolder, string passId,
		string profileId, string profileSettings, int repeat)
	{
		string passFolder = IOPath.Combine(runFolder, Sanitise(passId));
		Directory.CreateDirectory(passFolder);

		File.WriteAllText(IOPath.Combine(passFolder, "frames.csv"), BuildFramesCsv(runner));

		SegmentStats[] segments = ComputeSegments(runner);
		File.WriteAllText(IOPath.Combine(passFolder, "segments.csv"),
			BuildSegmentsCsv(runner, segments));

		return new PassResult
		{
			passId = passId,
			profileId = profileId,
			profileSettings = profileSettings,
			repeat = repeat,
			poseHash = runner.ObservedPoseHash,
			sceneHash = ComputeSceneHash(runner),
			segments = segments,
			geometry = CollectGeometry(runner)
		};
	}

	static FrameGeometry[] CollectGeometry(BenchmarkRunner runner)
	{
		int limit = Mathf.Min(runner.FrameCursor, runner.Records.Length);
		var result = new List<FrameGeometry>(limit);

		for (int i = 0; i < limit; i++)
		{
			if (runner.Plan.frames[i].phase != BenchmarkPhase.Measure) { continue; }

			FrameSampler.Sample s = runner.Records[i].sample;
			result.Add(new FrameGeometry
			{
				drawCalls = s.drawCalls,
				triangles = s.triangles,
				lodHighRes = runner.Records[i].lodHighResCount,
				wallMs = s.wallMs
			});
		}

		return result.ToArray();
	}

	/// <summary>Writes run.json and summary.md once every pass is done.</summary>
	public static void WriteRunSummary(BenchmarkRunner runner, string runFolder,
		List<PassResult> passes, string machineLabel)
	{
		GitInfo.TryGet(out string commit, out string branch, out bool dirty);

		RunMetadata metadata = BuildMetadata(runner, commit, branch, dirty, machineLabel,
			IOPath.GetFileName(runFolder), passes);

		File.WriteAllText(IOPath.Combine(runFolder, "run.json"), JsonUtility.ToJson(metadata, true));
		File.WriteAllText(IOPath.Combine(runFolder, "summary.md"),
			BuildSummary(metadata, passes));

		if (runner.mode == BenchmarkRunMode.SelfCheck)
		{
			File.WriteAllText(IOPath.Combine(runFolder, "selfcheck.md"),
				BuildSelfCheck(metadata, passes));
		}
	}

}
