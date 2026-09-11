using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// The project defines its own `Path` in the global namespace - a polygon path, in
// Assets/Scripts/Geo/Shape.cs - and a global-namespace type shadows a using-imported
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
///
/// Split across several files by concern - this one holds shared types and the run-folder
/// setup; BenchmarkWriter.Batches.cs, .SelfCheck.cs, .Frames.cs, .Stats.cs, .RunJson.cs and
/// .Summary.cs hold the rest, one file per section this used to have as a `// ----` divider
/// in a single 1500-line file. All still one compiled type - `partial` costs nothing here
/// since nothing outside this class calls into any of these pieces by file.
/// </summary>
public static partial class BenchmarkWriter
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
		return IOPath.GetFullPath( IOPath.Combine( Application.dataPath, "..", folder ) );
	}

	/// <summary>
	/// The geometry a single measured frame submitted, retained per pass.
	///
	/// Kept so a self-check can report *how many* frames disagreed rather than only that the
	/// scene hash did. One anomalous frame out of 2400 and a systematic difference produce
	/// the same hash mismatch, and they mean entirely different things.
	/// </summary>
	public struct FrameGeometry
	{
		public long drawCalls, triangles;
		public int lodHighRes;
		public double wallMs;
	}

	/// <summary>One completed pass, kept so the run-level summary can compare them.</summary>
	public class PassResult
	{
		public string passId, profileId, profileSettings;
		public int repeat;
		public ulong poseHash, sceneHash;
		public SegmentStats[] segments;
		public FrameGeometry[] geometry;
	}

	/// <summary>
	/// Creates the run folder and writes the shared plan. Called once, before the first
	/// pass - the plan is common to every pass by construction, which is what makes the
	/// passes comparable.
	/// </summary>
	public static string BeginRun( BenchmarkPlan plan, string outputRoot, BenchmarkRunMode mode )
	{
		GitInfo.TryGet( out string commit, out _, out _ );

		string stamp = System.DateTime.Now.ToString( "yyyyMMdd-HHmmss", Ci );
		string shortCommit = string.IsNullOrEmpty( commit ) ? "nogit" : commit;
		// Non-timing runs are tagged in the folder name: they sit next to timing runs of the
		// same benchmark and contain a frames.csv that looks identical - a capture run's
		// carries readback stalls, a self-check's is deliberately redundant. Nobody should
		// have to open run.json to tell them apart.
		string suffix = mode == BenchmarkRunMode.Capture ? "_capture"
			: mode == BenchmarkRunMode.SelfCheck ? "_selfcheck" : "";
		string runFolder = IOPath.Combine( outputRoot,
			$"{stamp}_{Sanitise( plan.definition.id )}_{shortCommit}{suffix}" );

		Directory.CreateDirectory( runFolder );
		File.WriteAllText( IOPath.Combine( runFolder, "plan.csv" ), plan.ToCsv() );
		return runFolder;
	}

}
