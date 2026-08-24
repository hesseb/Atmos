using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// See BenchmarkWriter.cs for the shadowing note this alias exists for.
using IOPath = System.IO.Path;

public static partial class BenchmarkWriter
{
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

}
