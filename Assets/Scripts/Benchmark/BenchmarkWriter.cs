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
		public string[] warnings;
	}

	// ------------------------------------------------------------------ writing

	public static string DefaultOutputRoot()
	{
		string folder = Application.isEditor ? "Results" : "BenchmarkResults";
		return IOPath.GetFullPath(IOPath.Combine(Application.dataPath, "..", folder));
	}

	/// <summary>Writes a run. Returns the run folder, or null on failure.</summary>
	public static string Write(BenchmarkRunner runner, string outputRoot, string passId,
		string machineLabel)
	{
		if (runner?.Plan == null || runner.Records == null) { return null; }

		try
		{
			GitInfo.TryGet(out string commit, out string branch, out bool dirty);

			string stamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss", Ci);
			string shortCommit = string.IsNullOrEmpty(commit) ? "nogit" : commit;
			string runFolder = IOPath.Combine(outputRoot,
				$"{stamp}_{Sanitise(runner.Plan.definition.id)}_{shortCommit}");
			string passFolder = IOPath.Combine(runFolder, Sanitise(passId));

			Directory.CreateDirectory(passFolder);

			File.WriteAllText(IOPath.Combine(runFolder, "plan.csv"), runner.Plan.ToCsv());
			File.WriteAllText(IOPath.Combine(passFolder, "frames.csv"), BuildFramesCsv(runner));

			SegmentStats[] segments = ComputeSegments(runner);
			File.WriteAllText(IOPath.Combine(passFolder, "segments.csv"), BuildSegmentsCsv(runner, segments));

			RunMetadata metadata = BuildMetadata(runner, commit, branch, dirty, machineLabel,
				IOPath.GetFileName(runFolder));
			File.WriteAllText(IOPath.Combine(runFolder, "run.json"), JsonUtility.ToJson(metadata, true));
			File.WriteAllText(IOPath.Combine(runFolder, "summary.md"),
				BuildSummary(runner, metadata, segments, passId));

			return runFolder;
		}
		catch (System.Exception e)
		{
			Debug.LogError($"[Benchmark] failed to write results: {e}");
			return null;
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
			  .Append(p.phase == BenchmarkPhase.Measure ? '1' : '0').Append(',')
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

	static RunMetadata BuildMetadata(BenchmarkRunner runner, string commit, string branch,
		bool dirty, string machineLabel, string runId)
	{
		BenchmarkPlan plan = runner.Plan;
		BenchmarkSceneRefs refs = runner.sceneRefs;
		Camera cam = refs.camera;

		int measured = 0;
		foreach (PlannedFrame f in plan.frames)
		{
			if (f.phase == BenchmarkPhase.Measure) { measured++; }
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
			is_editor = Application.isEditor,
			development_build = Debug.isDebugBuild,
			authoritative = !Application.isEditor,

			benchmark = new BenchmarkMeta
			{
				asset_name = plan.definition.name,
				id = plan.definition.id,
				description = plan.definition.description,
				plan_hash = "0x" + plan.planHash.ToString("x16"),
				pose_hash = "0x" + runner.ObservedPoseHash.ToString("x16"),
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

			warnings = new List<string>(runner.Warnings).ToArray()
		};
	}

	// -------------------------------------------------------------- summary.md

	static string BuildSummary(BenchmarkRunner runner, RunMetadata meta, SegmentStats[] segments,
		string passId)
	{
		var sb = new StringBuilder();
		sb.Append("# Benchmark run `").Append(meta.run_id).Append("`\n\n");

		if (!meta.authoritative)
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
		  .Append("| frames | ").Append(meta.benchmark.total_frames.ToString(Ci))
		  .Append(" total, ").Append(meta.benchmark.measured_frames.ToString(Ci)).Append(" measured |\n")
		  .Append("| resolution | ").Append(meta.resolution.actual_width.ToString(Ci)).Append('x')
		  .Append(meta.resolution.actual_height.ToString(Ci))
		  .Append(meta.resolution.matched ? "" : " (**requested " + meta.resolution.requested_width + "x"
			  + meta.resolution.requested_height + "**)").Append(" |\n")
		  .Append("| MSAA | ").Append(meta.quality.anti_aliasing.ToString(Ci)).Append("x |\n")
		  .Append("| GPU | ").Append(meta.hardware.graphics_device).Append(" (")
		  .Append(meta.hardware.graphics_api).Append(") |\n")
		  .Append("| Unity | ").Append(meta.unity_version).Append(" |\n")
		  .Append("| commit | `").Append(meta.git_commit).Append(meta.git_dirty ? " (dirty)" : "").Append("` |\n\n");

		sb.Append("## Per segment, pass `").Append(passId).Append("`\n\n");
		sb.Append("GPU frame time in ms. The 1% low is the **mean of the slowest 1% of frames**; ")
		  .Append("p99 is the same tail expressed as a percentile.\n\n");
		sb.Append("| segment | frames | sky frac | median | mean | p95 | p99 | 1% low | max |\n")
		  .Append("|---|---|---|---|---|---|---|---|---|\n");

		foreach (SegmentStats s in segments)
		{
			sb.Append("| ").Append(s.label)
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

		sb.Append("\n### CPU frame time (ms)\n\n")
		  .Append("| segment | median | mean | p99 | avg fps |\n|---|---|---|---|---|\n");
		foreach (SegmentStats s in segments)
		{
			sb.Append("| ").Append(s.label)
			  .Append(" | ").Append(M(s.cpu.medianMs))
			  .Append(" | ").Append(M(s.cpu.meanMs))
			  .Append(" | ").Append(M(s.cpu.p99Ms))
			  .Append(" | ").Append(M(s.cpu.avgFps))
			  .Append(" |\n");
		}

		if (meta.warnings != null && meta.warnings.Length > 0)
		{
			sb.Append("\n## Caveats\n\n");
			foreach (string w in meta.warnings) { sb.Append("- `").Append(w).Append("`\n"); }
		}

		return sb.ToString();
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
