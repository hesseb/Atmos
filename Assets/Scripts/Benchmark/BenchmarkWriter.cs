using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// The project defines its own `Path` in the global namespace - a polygon path, in
// Assets/Scripts/Types/Shape.cs - and a global-namespace type shadows a using-imported
// one, so plain `Path` here resolves to that struct rather than System.IO.Path. Alias it.
using IOPath = System.IO.Path;

/// <summary>
/// Writes a completed run to disk: per-frame CSV, per-segment statistics, run metadata and
/// a human-readable summary.
///
/// Everything is buffered and written once at the end of a pass - no per-frame I/O, which
/// would land in the very numbers being measured.
///
/// Every numeric format goes through InvariantCulture. This machine is sv-SE, where the
/// default ToString emits "1,5", which would produce CSVs that parse without error and are
/// entirely wrong.
/// </summary>
public static class BenchmarkWriter
{
	static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

	// ------------------------------------------------------------------ metadata

	[System.Serializable] public class CounterAvailabilityEntry { public string name; public bool available; }

	[System.Serializable]
	public class BenchmarkMeta
	{
		public string asset_name, id, description;
		public string plan_hash, pose_hash;
		public int total_frames, measured_frames, segment_count;
		public float simulated_fps, capture_delta_time;
		public int boot_frames, warmup_frames, default_settle_frames, flush_frames;
		public bool used_live_bookmarks;
	}

	[System.Serializable]
	public class ResolutionMeta
	{
		public int requested_width, requested_height, actual_width, actual_height;
		public bool matched;
		public string fullscreen_mode;
	}

	[System.Serializable]
	public class QualityMeta
	{
		public string level_name, colour_space, rendering_path;
		public int anti_aliasing, vsync_count, shadow_resolution, shadow_cascades;
		public float shadow_distance, camera_near, camera_far, camera_fov;
		public bool camera_hdr, camera_msaa;
	}

	[System.Serializable]
	public class SceneMeta
	{
		public int lod_frames_per_update, lod_group_count;
		public string lod_mode;
		public float lod_high_res_threshold;
		public bool country_ui_active, solar_animate, geocentric;
	}

	[System.Serializable]
	public class HardwareMeta
	{
		public string graphics_device, graphics_api, graphics_vendor, graphics_version;
		public int graphics_memory_mb, shader_level;
		public string processor;
		public int processor_count, processor_frequency, system_memory_mb;
		public string operating_system;
	}

	[System.Serializable]
	public class InstrumentationMeta
	{
		public bool frame_timing_available;
		public int gpu_timing_lag_frames, attribution_anomalies;
		public string timing_alignment;
		public CounterAvailabilityEntry[] counters_available;
	}

	[System.Serializable]
	public class RunMetadata
	{
		public string schema_version = "1";
		public string run_id, started_utc, machine_label;
		public string git_commit, git_branch;
		public bool git_dirty;
		public string unity_version, product_version;
		/// <summary>"Timing", "Capture" or "SelfCheck". A capture run reports no statistics; a
		/// self-check additionally emits selfcheck.md with the run-to-run noise floor.</summary>
		public string run_mode;
		public int screenshots_captured;
		public bool is_editor, development_build;
		/// <summary>False for editor runs: editor overhead and an unpinned Game view size
		/// make those indicative only.</summary>
		public bool authoritative;
		public BenchmarkMeta benchmark;
		public ResolutionMeta resolution;
		public QualityMeta quality;
		public SceneMeta scene;
		public HardwareMeta hardware;
		public InstrumentationMeta instrumentation;
		public PassMeta[] passes;
		public string[] warnings;
	}

	// ------------------------------------------------------------------ writing

	public static string DefaultOutputRoot()
	{
		string folder = Application.isEditor ? "Results" : "BenchmarkResults";
		return IOPath.GetFullPath(IOPath.Combine(Application.dataPath, "..", folder));
	}

	/// <summary>One completed pass, kept so the run-level summary can compare them.</summary>
	public class PassResult
	{
		public string passId, profileId, profileSettings;
		public int repeat;
		public ulong poseHash, sceneHash;
		public SegmentStats[] segments;
	}

	/// <summary>
	/// Creates the run folder and writes the shared plan. Called once, before the first
	/// pass - the plan is common to every pass by construction, which is what makes the
	/// passes comparable.
	/// </summary>
	public static string BeginRun(BenchmarkPlan plan, string outputRoot, BenchmarkRunMode mode)
	{
		GitInfo.TryGet(out string commit, out _, out _);

		string stamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss", Ci);
		string shortCommit = string.IsNullOrEmpty(commit) ? "nogit" : commit;
		// Non-timing runs are tagged in the folder name: they sit next to timing runs of the
		// same benchmark and contain a frames.csv that looks identical - a capture run's
		// carries readback stalls, a self-check's is deliberately redundant. Nobody should
		// have to open run.json to tell them apart.
		string suffix = mode == BenchmarkRunMode.Capture ? "_capture"
			: mode == BenchmarkRunMode.SelfCheck ? "_selfcheck" : "";
		string runFolder = IOPath.Combine(outputRoot,
			$"{stamp}_{Sanitise(plan.definition.id)}_{shortCommit}{suffix}");

		Directory.CreateDirectory(runFolder);
		File.WriteAllText(IOPath.Combine(runFolder, "plan.csv"), plan.ToCsv());
		return runFolder;
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
			segments = segments
		};
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

		bool scenesAgree = true;
		foreach (PassResult a in passes)
		{
			foreach (PassResult b in passes)
			{
				// Only within a profile: two profiles legitimately draw different geometry,
				// and that difference is a result rather than a fault.
				if (a.profileId == b.profileId && a.sceneHash != b.sceneHash) { scenesAgree = false; }
			}
		}

		sb.Append("| check | result | meaning |\n|---|---|---|\n")
		  .Append("| pose hash identical across all passes | ")
		  .Append(posesAgree ? "**PASS**" : "**FAIL**")
		  .Append(" | every pass rendered the same camera and sun sequence |\n")
		  .Append("| scene hash identical within each profile | ")
		  .Append(scenesAgree ? "**PASS**" : "**FAIL**")
		  .Append(" | repeats submitted the same geometry workload |\n\n");

		if (!posesAgree || !scenesAgree)
		{
			sb.Append("> **A comparability check failed.** The spread below is not a noise ")
			  .Append("floor - the repeats were not measuring the same thing. Fix this first.\n\n");
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

	// -------------------------------------------------------------- frames.csv

	static string BuildFramesCsv(BenchmarkRunner runner)
	{
		BenchmarkPlan plan = runner.Plan;
		int count = Mathf.Min(runner.FrameCursor, runner.Records.Length);
		var sb = new StringBuilder(count * 320);

		// A capture run's frame times carry the screenshot readback stalls, so no row from it
		// is measured. The rows are still written - the pose columns are the evidence that
		// the captured images correspond to the timing run's frames.
		bool measuredRun = !runner.IsCaptureRun;

		sb.Append("frame_index,phase,segment_index,segment_label,segment_frame,measured,")
		  .Append("sim_time_s,wall_ms,delta_ms,")
		  .Append("cpu_frame_ms,cpu_main_ms,cpu_render_ms,cpu_present_wait_ms,gpu_ms,timing_valid,")
		  .Append("draw_calls,batches,setpass_calls,triangles,vertices,shadow_casters,")
		  .Append("gc_alloc_bytes,mem_total_used,mem_total_reserved,mem_gfx_used,mem_system_used,mem_gc_used,")
		  .Append("plan_lon,plan_lat,plan_alt,plan_pitch,plan_heading,plan_roll,plan_fov,")
		  .Append("cam_pos_x,cam_pos_y,cam_pos_z,cam_fwd_x,cam_fwd_y,cam_fwd_z,")
		  .Append("sun_dayT,sun_monthT,sun_yearT,sun_dir_x,sun_dir_y,sun_dir_z,")
		  .Append("lod_high_res,sky_fraction,screenshot\n");

		float dt = plan.CaptureDeltaTime;

		for (int i = 0; i < count; i++)
		{
			PlannedFrame p = plan.frames[i];
			BenchmarkRunner.FrameRecord r = runner.Records[i];
			FrameSampler.Sample s = r.sample;

			string label = p.segmentIndex >= 0 && p.segmentIndex < plan.segmentLabels.Length
				? plan.segmentLabels[p.segmentIndex] : "";

			sb.Append(i.ToString(Ci)).Append(',')
			  .Append(p.phase).Append(',')
			  .Append(p.segmentIndex.ToString(Ci)).Append(',')
			  .Append(label).Append(',')
			  .Append(p.segmentFrame.ToString(Ci)).Append(',')
			  .Append(measuredRun && p.phase == BenchmarkPhase.Measure ? '1' : '0').Append(',')
			  .Append(F(i * dt)).Append(',')
			  .Append(F(s.wallMs)).Append(',')
			  .Append(F(r.deltaMs)).Append(',')
			  .Append(F(s.cpuFrameMs)).Append(',')
			  .Append(F(s.cpuMainMs)).Append(',')
			  .Append(F(s.cpuRenderMs)).Append(',')
			  .Append(F(s.cpuPresentWaitMs)).Append(',')
			  .Append(F(s.gpuMs)).Append(',')
			  .Append(s.timingValid ? '1' : '0').Append(',')
			  .Append(L(s.drawCalls)).Append(',')
			  .Append(L(s.batches)).Append(',')
			  .Append(L(s.setPassCalls)).Append(',')
			  .Append(L(s.triangles)).Append(',')
			  .Append(L(s.vertices)).Append(',')
			  .Append(L(s.shadowCasters)).Append(',')
			  .Append(L(s.gcAllocBytes)).Append(',')
			  .Append(L(s.totalUsedBytes)).Append(',')
			  .Append(L(s.totalReservedBytes)).Append(',')
			  .Append(L(s.gfxUsedBytes)).Append(',')
			  .Append(L(s.systemUsedBytes)).Append(',')
			  .Append(L(s.gcUsedBytes)).Append(',')
			  .Append(F(p.view.coordinate.longitude)).Append(',')
			  .Append(F(p.view.coordinate.latitude)).Append(',')
			  .Append(F(p.view.altitude)).Append(',')
			  .Append(F(p.view.pitch)).Append(',')
			  .Append(F(p.view.heading)).Append(',')
			  .Append(F(p.view.roll)).Append(',')
			  .Append(F(p.view.fieldOfView)).Append(',')
			  .Append(F(r.cameraPosition.x)).Append(',')
			  .Append(F(r.cameraPosition.y)).Append(',')
			  .Append(F(r.cameraPosition.z)).Append(',')
			  .Append(F(r.cameraForward.x)).Append(',')
			  .Append(F(r.cameraForward.y)).Append(',')
			  .Append(F(r.cameraForward.z)).Append(',')
			  .Append(F(p.dayT)).Append(',')
			  .Append(F(p.monthT)).Append(',')
			  .Append(F(p.yearT)).Append(',')
			  .Append(F(r.sunDirection.x)).Append(',')
			  .Append(F(r.sunDirection.y)).Append(',')
			  .Append(F(r.sunDirection.z)).Append(',')
			  .Append(r.lodHighResCount.ToString(Ci)).Append(',')
			  .Append(F(p.skyFraction)).Append(',')
			  .Append(p.screenshot ? '1' : '0').Append('\n');
		}

		return sb.ToString();
	}

	static string F(double v) => v.ToString("G9", Ci);
	static string F(float v) => v.ToString("G9", Ci);
	// -1 marks an unavailable counter; emitted as an empty cell so it can never be read
	// as a genuine zero.
	static string L(long v) => v < 0 ? "" : v.ToString(Ci);

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

	// -------------------------------------------------------------- run.json

	[System.Serializable]
	public class PassMeta
	{
		public string pass_id, profile_id, profile_settings, pose_hash, scene_hash;
		public int repeat;
	}

	static RunMetadata BuildMetadata(BenchmarkRunner runner, string commit, string branch,
		bool dirty, string machineLabel, string runId, List<PassResult> passes)
	{
		BenchmarkPlan plan = runner.Plan;
		BenchmarkSceneRefs refs = runner.sceneRefs;
		Camera cam = refs.camera;

		int measured = 0;
		if (!runner.IsCaptureRun)
		{
			foreach (PlannedFrame f in plan.frames)
			{
				if (f.phase == BenchmarkPhase.Measure) { measured++; }
			}
		}

		var counters = new List<CounterAvailabilityEntry>();
		if (runner.CounterAvailability != null)
		{
			foreach ((string name, bool available) in runner.CounterAvailability)
			{
				counters.Add(new CounterAvailabilityEntry { name = name, available = available });
			}
		}

		bool resolutionMatched = Screen.width == runner.targetResolution.x
			&& Screen.height == runner.targetResolution.y;

		return new RunMetadata
		{
			run_id = runId,
			started_utc = System.DateTime.UtcNow.ToString("o", Ci),
			machine_label = machineLabel,
			git_commit = commit,
			git_branch = branch,
			git_dirty = dirty,
			unity_version = Application.unityVersion,
			product_version = Application.version,
			run_mode = runner.mode.ToString(),
			screenshots_captured = runner.ScreenshotsCaptured,
			is_editor = Application.isEditor,
			development_build = Debug.isDebugBuild,
			// A capture run is never authoritative regardless of where it ran: every frame it
			// timed includes a readback stall.
			authoritative = !Application.isEditor && !runner.IsCaptureRun,

			benchmark = new BenchmarkMeta
			{
				asset_name = plan.definition.name,
				id = plan.definition.id,
				description = plan.definition.description,
				plan_hash = "0x" + plan.planHash.ToString("x16"),
				pose_hash = passes != null && passes.Count > 0
					? "0x" + passes[0].poseHash.ToString("x16") : "",
				total_frames = plan.Length,
				measured_frames = measured,
				segment_count = plan.segmentLabels.Length,
				simulated_fps = plan.definition.simulatedFps,
				capture_delta_time = plan.CaptureDeltaTime,
				boot_frames = plan.definition.bootFrames,
				warmup_frames = plan.definition.warmupFrames,
				default_settle_frames = plan.definition.defaultSettleFrames,
				flush_frames = plan.definition.flushFrames,
				used_live_bookmarks = plan.usedLiveBookmarks
			},

			resolution = new ResolutionMeta
			{
				requested_width = runner.targetResolution.x,
				requested_height = runner.targetResolution.y,
				actual_width = Screen.width,
				actual_height = Screen.height,
				matched = resolutionMatched,
				fullscreen_mode = Screen.fullScreenMode.ToString()
			},

			quality = new QualityMeta
			{
				level_name = QualitySettings.names.Length > 0
					? QualitySettings.names[QualitySettings.GetQualityLevel()] : "?",
				colour_space = QualitySettings.activeColorSpace.ToString(),
				rendering_path = cam != null ? cam.actualRenderingPath.ToString() : "?",
				anti_aliasing = QualitySettings.antiAliasing,
				vsync_count = QualitySettings.vSyncCount,
				shadow_resolution = (int)QualitySettings.shadowResolution,
				shadow_cascades = QualitySettings.shadowCascades,
				shadow_distance = QualitySettings.shadowDistance,
				camera_near = cam != null ? cam.nearClipPlane : 0,
				camera_far = cam != null ? cam.farClipPlane : 0,
				camera_fov = cam != null ? cam.fieldOfView : 0,
				camera_hdr = cam != null && cam.allowHDR,
				camera_msaa = cam != null && cam.allowMSAA
			},

			scene = new SceneMeta
			{
				lod_frames_per_update = refs.lodSystem != null ? refs.lodSystem.numFramesPerUpdate : -1,
				lod_group_count = refs.lodSystem != null ? refs.lodSystem.RenderGroupCount : -1,
				lod_mode = refs.lodSystem != null ? refs.lodSystem.mode.ToString() : "?",
				lod_high_res_threshold = refs.lodSystem != null ? refs.lodSystem.highResDistanceThreshold : 0,
				country_ui_active = refs.countryInteraction != null && refs.countryInteraction.activeSelf,
				solar_animate = refs.solarSystem != null && refs.solarSystem.animate,
				geocentric = refs.solarSystem != null && refs.solarSystem.geocentric
			},

			hardware = new HardwareMeta
			{
				graphics_device = SystemInfo.graphicsDeviceName,
				graphics_api = SystemInfo.graphicsDeviceType.ToString(),
				graphics_vendor = SystemInfo.graphicsDeviceVendor,
				graphics_version = SystemInfo.graphicsDeviceVersion,
				graphics_memory_mb = SystemInfo.graphicsMemorySize,
				shader_level = SystemInfo.graphicsShaderLevel,
				processor = SystemInfo.processorType,
				processor_count = SystemInfo.processorCount,
				processor_frequency = SystemInfo.processorFrequency,
				system_memory_mb = SystemInfo.systemMemorySize,
				operating_system = SystemInfo.operatingSystem
			},

			instrumentation = new InstrumentationMeta
			{
				frame_timing_available = runner.FrameTimingAvailable,
				gpu_timing_lag_frames = runner.TimingLagFrames,
				attribution_anomalies = runner.AttributionAnomalies,
				timing_alignment = runner.AttributionAnomalies == 0 ? "exact" : "approximate",
				counters_available = counters.ToArray()
			},

			passes = BuildPassMeta(passes),
			warnings = new List<string>(runner.Warnings).ToArray()
		};
	}

	static PassMeta[] BuildPassMeta(List<PassResult> passes)
	{
		if (passes == null) { return new PassMeta[0]; }

		var result = new PassMeta[passes.Count];
		for (int i = 0; i < passes.Count; i++)
		{
			result[i] = new PassMeta
			{
				pass_id = passes[i].passId,
				profile_id = passes[i].profileId,
				profile_settings = passes[i].profileSettings,
				repeat = passes[i].repeat,
				pose_hash = "0x" + passes[i].poseHash.ToString("x16"),
				scene_hash = "0x" + passes[i].sceneHash.ToString("x16")
			};
		}
		return result;
	}

	// -------------------------------------------------------------- summary.md

	static string BuildSummary(RunMetadata meta, List<PassResult> passes)
	{
		var sb = new StringBuilder();
		sb.Append("# Benchmark run `").Append(meta.run_id).Append("`\n\n");

		bool capture = meta.run_mode == BenchmarkRunMode.Capture.ToString();

		if (capture)
		{
			sb.Append("> **Capture run - no measurements.** This run exists to produce images.\n")
			  .Append("> Every screenshot forces a GPU-to-CPU readback that stalls the frame it\n")
			  .Append("> is taken on, so the frame times here are meaningless and every row is\n")
			  .Append("> marked `measured = 0`. The matching numbers come from a Timing run of\n")
			  .Append("> the same benchmark; pair them by frame index after checking that the\n")
			  .Append("> plan and pose hashes agree.\n\n");
		}

		if (meta.is_editor)
		{
			sb.Append("> **Editor run - not authoritative.** Editor overhead and an unpinned Game\n")
			  .Append("> view size make these numbers indicative only. Use a standalone build for\n")
			  .Append("> anything that goes in the report.\n\n");
		}

		if (meta.git_dirty)
		{
			sb.Append("> **Working tree was dirty.** These numbers were produced from uncommitted\n")
			  .Append("> code and are not reproducible from `").Append(meta.git_commit).Append("` alone.\n\n");
		}

		if (!meta.instrumentation.frame_timing_available)
		{
			sb.Append("> **No GPU timing.** Frame Timing Stats is disabled in Player Settings, so\n")
			  .Append("> the GPU columns are empty and only CPU-side numbers are available.\n\n");
		}

		sb.Append("| | |\n|---|---|\n")
		  .Append("| benchmark | `").Append(meta.benchmark.id).Append("` |\n")
		  .Append("| plan hash | `").Append(meta.benchmark.plan_hash).Append("` |\n")
		  .Append("| pose hash | `").Append(meta.benchmark.pose_hash).Append("` |\n")
		  .Append("| mode | ").Append(meta.run_mode).Append(" |\n")
		  .Append("| frames | ").Append(meta.benchmark.total_frames.ToString(Ci))
		  .Append(" total, ").Append(meta.benchmark.measured_frames.ToString(Ci)).Append(" measured |\n")
		  .Append(capture ? "| screenshots | " + meta.screenshots_captured.ToString(Ci) + " |\n" : "")
		  .Append("| resolution | ").Append(meta.resolution.actual_width.ToString(Ci)).Append('x')
		  .Append(meta.resolution.actual_height.ToString(Ci))
		  .Append(meta.resolution.matched ? "" : " (**requested " + meta.resolution.requested_width + "x"
			  + meta.resolution.requested_height + "**)").Append(" |\n")
		  .Append("| MSAA | ").Append(meta.quality.anti_aliasing.ToString(Ci)).Append("x |\n")
		  .Append("| GPU | ").Append(meta.hardware.graphics_device).Append(" (")
		  .Append(meta.hardware.graphics_api).Append(") |\n")
		  .Append("| Unity | ").Append(meta.unity_version).Append(" |\n")
		  .Append("| commit | `").Append(meta.git_commit).Append(meta.git_dirty ? " (dirty)" : "").Append("` |\n\n");

		// A capture run has no segment statistics at all, so the timing tables would render
		// as bare headers. Point at the images instead.
		if (capture)
		{
			sb.Append("## Screenshots\n\n")
			  .Append("`screenshots/manifest.csv` lists every image with the frame index, ")
			  .Append("segment, camera pose, sun elevation and sky fraction that produced it. ")
			  .Append("Filenames are `f<frame>_<pass>_<segment>.png`, so the same frame across ")
			  .Append("two renderer profiles differs only in the pass component.\n\n");

			AppendReproducibility(sb, passes);
			AppendCaveats(sb, meta);
			return sb.ToString();
		}

		// --- per pass, per segment ---
		sb.Append("## GPU frame time (ms)\n\n")
		  .Append("The 1% low is the **mean of the slowest 1% of frames**; p99 is the same tail ")
		  .Append("expressed as a percentile. They can differ substantially - that is why both ")
		  .Append("are reported.\n\n");

		sb.Append("| pass | segment | frames | sky frac | median | mean | p95 | p99 | 1% low | max |\n")
		  .Append("|---|---|---|---|---|---|---|---|---|---|\n");

		foreach (PassResult pass in passes)
		{
			foreach (SegmentStats s in pass.segments)
			{
				sb.Append("| ").Append(pass.passId)
				  .Append(" | ").Append(s.label)
				  .Append(" | ").Append(s.frames.ToString(Ci))
				  .Append(" | ").Append(s.meanSkyFraction.ToString("F2", Ci))
				  .Append(" | ").Append(M(s.gpu.medianMs))
				  .Append(" | ").Append(M(s.gpu.meanMs))
				  .Append(" | ").Append(M(s.gpu.p95Ms))
				  .Append(" | ").Append(M(s.gpu.p99Ms))
				  .Append(" | ").Append(M(s.gpu.p1LowMeanMs))
				  .Append(" | ").Append(M(s.gpu.maxMs))
				  .Append(" |\n");
			}
		}

		sb.Append("\n## CPU frame time (ms)\n\n")
		  .Append("| pass | segment | median | mean | p99 | avg fps |\n|---|---|---|---|---|---|\n");
		foreach (PassResult pass in passes)
		{
			foreach (SegmentStats s in pass.segments)
			{
				sb.Append("| ").Append(pass.passId)
				  .Append(" | ").Append(s.label)
				  .Append(" | ").Append(M(s.cpu.medianMs))
				  .Append(" | ").Append(M(s.cpu.meanMs))
				  .Append(" | ").Append(M(s.cpu.p99Ms))
				  .Append(" | ").Append(M(s.cpu.avgFps))
				  .Append(" |\n");
			}
		}

		AppendComparison(sb, passes);
		AppendReproducibility(sb, passes);

		if (meta.run_mode == BenchmarkRunMode.SelfCheck.ToString())
		{
			sb.Append("\nSee `selfcheck.md` for the run-to-run spread these repeats disagreed ")
			  .Append("by - the margin any delta above has to clear to mean anything.\n");
		}

		AppendCaveats(sb, meta);

		return sb.ToString();
	}

	static void AppendCaveats(StringBuilder sb, RunMetadata meta)
	{
		if (meta.warnings == null || meta.warnings.Length == 0) { return; }

		sb.Append("\n## Caveats\n\n");
		foreach (string w in meta.warnings) { sb.Append("- `").Append(w).Append("`\n"); }
	}

	/// <summary>
	/// Profile-vs-profile deltas per segment, using the median across repeats so a single
	/// anomalous repeat cannot drive the headline number.
	/// </summary>
	static void AppendComparison(StringBuilder sb, List<PassResult> passes)
	{
		var profiles = new List<string>();
		foreach (PassResult p in passes)
		{
			if (!string.IsNullOrEmpty(p.profileId) && !profiles.Contains(p.profileId))
			{
				profiles.Add(p.profileId);
			}
		}
		if (profiles.Count < 2) { return; }

		string baseline = profiles[0];
		sb.Append("\n## Comparison (GPU median, ms)\n\n")
		  .Append("Median across repeats, per segment. Baseline is `").Append(baseline).Append("`.\n\n");

		sb.Append("| segment |");
		foreach (string p in profiles) { sb.Append(' ').Append(p).Append(" |"); }
		for (int i = 1; i < profiles.Count; i++)
		{
			sb.Append(' ').Append(profiles[i]).Append(" - ").Append(baseline).Append(" |");
		}
		sb.Append('\n').Append("|---|");
		for (int i = 0; i < profiles.Count + profiles.Count - 1; i++) { sb.Append("---|"); }
		sb.Append('\n');

		// Segment labels are shared across passes because the plan is shared.
		var labels = new List<string>();
		foreach (SegmentStats s in passes[0].segments)
		{
			if (!labels.Contains(s.label)) { labels.Add(s.label); }
		}

		foreach (string label in labels)
		{
			sb.Append("| ").Append(label).Append(" |");

			var medians = new List<double>();
			foreach (string profile in profiles)
			{
				double m = MedianOfRepeats(passes, profile, label);
				medians.Add(m);
				sb.Append(' ').Append(M(m)).Append(" |");
			}

			for (int i = 1; i < medians.Count; i++)
			{
				double delta = medians[i] - medians[0];
				string sign = delta >= 0 ? "+" : "";
				sb.Append(' ').Append(sign).Append(M(delta)).Append(" |");
			}
			sb.Append('\n');
		}
	}

	static double MedianOfRepeats(List<PassResult> passes, string profileId, string label)
	{
		var values = new List<double>();
		foreach (PassResult p in passes)
		{
			if (p.profileId != profileId) { continue; }
			foreach (SegmentStats s in p.segments)
			{
				if (s.label == label && s.gpu.Valid) { values.Add(s.gpu.medianMs); }
			}
		}

		if (values.Count == 0) { return double.NaN; }
		values.Sort();
		return BenchmarkStats.Percentile(values.ToArray(), 0.5);
	}

	/// <summary>
	/// Hash agreement and run-to-run spread.
	///
	/// The spread is the measurement noise floor: if a profile-vs-profile delta is not
	/// comfortably larger than it, the result is not significant. Better to learn that
	/// here than from an examiner.
	/// </summary>
	static void AppendReproducibility(StringBuilder sb, List<PassResult> passes)
	{
		sb.Append("\n## Reproducibility\n\n");

		bool posesAgree = true;
		for (int i = 1; i < passes.Count; i++)
		{
			if (passes[i].poseHash != passes[0].poseHash) { posesAgree = false; break; }
		}

		sb.Append(posesAgree
			? "- **Pose hash identical across every pass** - all passes rendered the same poses.\n"
			: "- **POSE HASH MISMATCH** - passes did not render the same poses. The comparison " +
			  "is invalid; something perturbed the camera or the sun.\n");

		sb.Append("\n| pass | profile | repeat | pose hash | scene hash |\n|---|---|---|---|---|\n");
		foreach (PassResult p in passes)
		{
			sb.Append("| ").Append(p.passId)
			  .Append(" | ").Append(p.profileId)
			  .Append(" | ").Append(p.repeat.ToString(Ci))
			  .Append(" | `0x").Append(p.poseHash.ToString("x16"))
			  .Append("` | `0x").Append(p.sceneHash.ToString("x16")).Append("` |\n");
		}

		sb.Append("\nScene hash covers draw calls, triangles and LOD state. It should match ")
		  .Append("between repeats of one profile, and may legitimately differ between ")
		  .Append("profiles by the passes each adds.\n");
	}

	static string M(double v) => double.IsNaN(v) ? "n/a" : v.ToString("F3", Ci);
}

/// <summary>
/// Commit identity for a run. A thesis figure produced from uncommitted code is not
/// reproducible, so the dirty flag matters as much as the hash.
/// </summary>
public static class GitInfo
{
	public static bool TryGet(out string commit, out string branch, out bool dirty)
	{
		commit = "unknown";
		branch = "unknown";
		dirty = false;

#if UNITY_EDITOR
		try
		{
			string root = IOPath.GetFullPath(IOPath.Combine(Application.dataPath, ".."));
			commit = Run(root, "rev-parse --short HEAD") ?? "unknown";
			branch = Run(root, "rev-parse --abbrev-ref HEAD") ?? "unknown";
			dirty = !string.IsNullOrEmpty(Run(root, "status --porcelain"));
			return commit != "unknown";
		}
		catch
		{
			return false;
		}
#else
		// A build would need a stamp baked in at build time; not yet implemented.
		return false;
#endif
	}

#if UNITY_EDITOR
	static string Run(string workingDirectory, string arguments)
	{
		var info = new System.Diagnostics.ProcessStartInfo("git", arguments)
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using (var process = System.Diagnostics.Process.Start(info))
		{
			if (process == null) { return null; }
			string output = process.StandardOutput.ReadToEnd();
			if (!process.WaitForExit(3000)) { return null; }
			return output.Trim();
		}
	}
#endif
}
