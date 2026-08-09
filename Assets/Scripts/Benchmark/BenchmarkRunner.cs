using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Plays a <see cref="BenchmarkPlan"/> one frame at a time.
///
/// Runs at execution order -500 so plan state is written before the things that consume
/// it. This has to be an Update state machine rather than a coroutine: SolarSystemManager
/// derives the sun at order 0, and a coroutine's `yield return null` resumes *after* all
/// Update calls, so a dayT written from a coroutine would land one frame late.
///
/// Per-frame order that results:
///   runner Update (-500)   write plan state for frame N
///   SolarSystemManager     derives sun/earth/moon/stars from it
///   RenderingManager       adds/removes the sky command buffer
///   TestbedCamera.LateUpdate  re-applies the same field values (idempotent)
///   Camera.onPreCull       LOD and atmosphere LUTs see a settled camera and sun
///   render, post chain
///   end of frame           read back the ACTUAL pose - ground truth for the pose hash
///
/// This step deliberately produces no files. Its job is to demonstrate the frame-lock
/// guarantee - exact frame count, pinned delta time, identical pose hash across runs -
/// before any statistics exist that could be quietly wrong.
/// </summary>
public enum BenchmarkRunMode
{
	/// <summary>Produce numbers. Frames marked for a screenshot are flagged in the CSV but
	/// nothing is captured, because the readback would corrupt their timings.</summary>
	Timing,
	/// <summary>Produce images. Captures marked frames and reports no statistics - every row
	/// is written with measured = 0 so a capture run can never be mistaken for a result.
	/// </summary>
	Capture
}

[DefaultExecutionOrder(-500)]
public class BenchmarkRunner : MonoBehaviour
{
	[Header("What to run")]
	public BenchmarkDefinition benchmark;
	public CameraBookmarks fallbackBookmarks;
	public BenchmarkSceneRefs sceneRefs = new BenchmarkSceneRefs();

	[Header("Renderer configurations")]
	// Leave empty to measure the scene as-authored. With two or more, the summary reports
	// the delta between them.
	public RendererProfile[] profiles;
	[Min(1)] public int repeats = 1;
	// Interleaved (ABAB) rather than grouped (AABB), so GPU thermal drift does not get
	// confounded with the renderer being compared.
	public bool interleaveRepeats = true;

	[Header("Setup")]
	// Timing runs produce the numbers, capture runs produce the images. They are separate
	// because a screenshot readback stalls the frame it is taken on. Run the same benchmark
	// in both modes and pair the outputs by frame index - the plan and pose hashes recorded
	// by each are what certify the two runs rendered the same thing.
	public BenchmarkRunMode mode = BenchmarkRunMode.Timing;
	public Vector2Int targetResolution = new Vector2Int(1920, 1080);
	public bool pinResolution = true;
	public bool runOnStart;

	[Header("Output")]
	public bool writeResults = true;
	// Free-text note recorded in run.json - which machine these numbers came from.
	public string machineLabel = "";
	// Leave empty for <project>/Results in the editor, <exe>/BenchmarkResults in a build.
	public string outputRootOverride = "";

	[Header("Debug")]
	public bool logProgress = true;

	public bool IsRunning { get; private set; }
	/// <summary>True when this run captures images instead of producing statistics. The
	/// writer consults it to blank the measured column - a capture run's frame times are
	/// polluted by readback stalls and must never reach a table.</summary>
	public bool IsCaptureRun => mode == BenchmarkRunMode.Capture;
	public ScreenshotCapture Screenshots { get; private set; }
	/// <summary>Survives the capture object being released, so run.json can record it.</summary>
	public int ScreenshotsCaptured { get; private set; }
	public int FrameCursor { get; private set; }
	public BenchmarkPlan Plan { get; private set; }
	public ulong ObservedPoseHash { get; private set; }
	public System.Action<BenchmarkRunner> onCompleted;

	/// <summary>Everything recorded for one frame: what was planned, what was observed,
	/// and what it cost.</summary>
	public struct FrameRecord
	{
		public Vector3 cameraPosition;
		public Vector3 cameraForward;
		public Vector3 sunDirection;
		public double deltaMs;
		public int lodHighResCount;
		public FrameSampler.Sample sample;
	}

	public FrameRecord[] Records { get; private set; }
	public FrameSampler Sampler { get; private set; }
	public IReadOnlyList<string> Warnings => warnings;

	// Instrumentation state, snapshotted before the sampler is disposed so the writer can
	// record what was and was not available.
	public bool FrameTimingAvailable { get; private set; }
	public int TimingLagFrames { get; private set; } = -1;
	public int AttributionAnomalies { get; private set; }
	public List<(string name, bool available)> CounterAvailability { get; private set; }

	RestoreScope scope;          // run-level: the pinned environment
	RestoreScope passScope;      // pass-level: the renderer profile
	readonly List<string> warnings = new List<string>();
	WaitForEndOfFrame waitForEndOfFrame;
	Coroutine endOfFrameRoutine;
	int deltaDriftFrames;
	double expectedDeltaMs;
	// Set by Update once it has written a frame's plan state, cleared by the end-of-frame
	// reader once it has recorded that frame. Keeps the two exactly in step.
	bool frameStateWritten;

	struct PassPlan { public RendererProfile profile; public int repeat; public string passId; }

	readonly List<PassPlan> passPlans = new List<PassPlan>();
	readonly List<BenchmarkWriter.PassResult> passResults = new List<BenchmarkWriter.PassResult>();
	int currentPass = -1;
	string runFolder;

	void Start()
	{
		if (runOnStart) { StartRun(); }
	}

	[ContextMenu("Start Run")]
	public void StartRun()
	{
		if (IsRunning)
		{
			Debug.LogWarning("[Benchmark] already running.", this);
			return;
		}

		if (benchmark == null)
		{
			Debug.LogError("[Benchmark] no BenchmarkDefinition assigned.", this);
			return;
		}

		if (!sceneRefs.Resolve(out string error))
		{
			Debug.LogError($"[Benchmark] {error}", this);
			return;
		}

		warnings.Clear();

		var settings = BenchmarkEnvironment.Defaults;
		settings.simulatedFps = benchmark.simulatedFps;
		settings.targetResolution = targetResolution;
		settings.pinResolution = pinResolution;

		BenchmarkEnvironment.Validate(sceneRefs, settings, warnings);

		float aspect = targetResolution.y > 0
			? (float)targetResolution.x / targetResolution.y
			: (float)Screen.width / Mathf.Max(1, Screen.height);

		Plan = BenchmarkPlan.Build(benchmark, sceneRefs.Earth,
			sceneRefs.testbedCamera.SurfaceRadius, aspect, fallbackBookmarks);

		if (Plan.Length == 0)
		{
			Debug.LogError("[Benchmark] plan is empty.", this);
			return;
		}

		warnings.AddRange(Plan.warnings);

		scope = BenchmarkEnvironment.Pin(sceneRefs, settings, warnings);

		BuildPassPlans();
		passResults.Clear();
		runFolder = null;

		// Checked before the run folder exists, so a misconfigured capture does not leave an
		// empty result directory behind.
		if (IsCaptureRun)
		{
			if (Plan.ScreenshotCount == 0)
			{
				Debug.LogError($"[Benchmark] capture run, but '{benchmark.id}' marks no frames " +
					"for a screenshot - it would replay the whole plan and produce nothing. Set " +
					"screenshotFirstAndLast or screenshotFrames on at least one segment.", this);
				scope?.Dispose();
				scope = null;
				return;
			}

			AddWarning("CAPTURE_RUN_NOT_MEASURED");
		}

		if (writeResults)
		{
			string root = string.IsNullOrEmpty(outputRootOverride)
				? BenchmarkWriter.DefaultOutputRoot()
				: outputRootOverride;
			runFolder = BenchmarkWriter.BeginRun(Plan, root, mode);

			if (IsCaptureRun) { Screenshots = new ScreenshotCapture(runFolder); }
		}
		else if (IsCaptureRun)
		{
			Debug.LogError("[Benchmark] capture run with writeResults off - there is nowhere " +
				"to put the images.", this);
			scope?.Dispose();
			scope = null;
			return;
		}

		ScreenshotsCaptured = 0;

		expectedDeltaMs = Plan.CaptureDeltaTime * 1000.0;
		IsRunning = true;
		waitForEndOfFrame = new WaitForEndOfFrame();
		endOfFrameRoutine = StartCoroutine(EndOfFrameLoop());

		Debug.Log($"[Benchmark] {Plan.Describe()}\n" +
			$"  passes: {passPlans.Count} ({string.Join(", ", passPlans.ConvertAll(p => p.passId))})\n" +
			$"  warnings: {(warnings.Count > 0 ? string.Join(", ", warnings) : "none")}", this);

		currentPass = -1;
		BeginNextPass();
	}

	/// <summary>
	/// Interleaved by default: with two profiles and three repeats the order is
	/// A0 B0 A1 B1 A2 B2, so GPU thermal drift affects both configurations equally
	/// instead of loading onto whichever ran second.
	/// </summary>
	void BuildPassPlans()
	{
		passPlans.Clear();

		// Repeats exist to average out measurement noise. A capture run has no measurements,
		// and every repeat would replay the plan to write byte-identical images over the
		// previous pass's - so it does exactly one pass per profile.
		int passRepeats = IsCaptureRun ? 1 : Mathf.Max(1, repeats);

		if (IsCaptureRun && repeats > 1)
		{
			Debug.Log($"[Benchmark] capture run: ignoring repeats={repeats}, one pass per profile.", this);
		}

		if (profiles == null || profiles.Length == 0)
		{
			for (int r = 0; r < passRepeats; r++)
			{
				passPlans.Add(new PassPlan { profile = null, repeat = r, passId = $"asis_r{r}" });
			}
			return;
		}

		if (interleaveRepeats)
		{
			for (int r = 0; r < passRepeats; r++)
			{
				foreach (RendererProfile profile in profiles)
				{
					if (profile == null) { continue; }
					passPlans.Add(new PassPlan { profile = profile, repeat = r, passId = $"{profile.id}_r{r}" });
				}
			}
		}
		else
		{
			foreach (RendererProfile profile in profiles)
			{
				if (profile == null) { continue; }
				for (int r = 0; r < passRepeats; r++)
				{
					passPlans.Add(new PassPlan { profile = profile, repeat = r, passId = $"{profile.id}_r{r}" });
				}
			}
		}
	}

	void BeginNextPass()
	{
		currentPass++;
		if (currentPass >= passPlans.Count)
		{
			FinishRun();
			return;
		}

		PassPlan pass = passPlans[currentPass];

		// Per-pass scope, so a profile's changes are undone before the next one applies.
		passScope = new RestoreScope();
		pass.profile?.Apply(sceneRefs, passScope);

		// Fresh recording state. The plan is replayed identically, including its boot,
		// prewarm and warmup phases - which is what absorbs the stale-LUT window and the
		// pipeline-state compilation that follow a renderer change.
		Records = new FrameRecord[Plan.Length];
		Sampler = new FrameSampler();
		FrameCursor = 0;
		deltaDriftFrames = 0;
		frameStateWritten = false;

		if (logProgress)
		{
			Debug.Log($"[Benchmark] pass {currentPass + 1}/{passPlans.Count}: {pass.passId}", this);
		}
	}

	void Update()
	{
		if (!IsRunning) { return; }

		// A sample taken now describes the frame that just completed, so it belongs to the
		// previous row. This is why the plan ends with flush frames: the last measured
		// frame's timing arrives during them.
		FrameSampler.Sample sample = Sampler.Capture();
		if (FrameCursor > 0 && FrameCursor - 1 < Records.Length)
		{
			Records[FrameCursor - 1].sample = sample;
		}

		if (FrameCursor >= Plan.Length)
		{
			EndPass();
			return;
		}

		PlannedFrame frame = Plan.frames[FrameCursor];

		// Sun first: SolarSystemManager reads these at order 0, just after us.
		sceneRefs.solarSystem.SetTimes(frame.dayT, frame.monthT, frame.yearT);
		// SetView applies the transform synchronously, so onPreCull this frame sees it.
		sceneRefs.testbedCamera.SetView(frame.view);

		// Hand off to the end-of-frame reader. Without this the reader would record any
		// frame it happened to see, including ones where this method never ran.
		frameStateWritten = true;
	}

	/// <summary>
	/// Reads back what actually happened, after rendering. The pose hash is built from the
	/// observed transform rather than the planned one - otherwise it would only prove the
	/// plan equals itself, and would miss a stray input or a camera regression.
	///
	/// Records a frame only once Update has written that frame's plan state. Two frames in
	/// a run have no such state: the one on which StartRun was invoked - a context menu
	/// fires mid-frame, so the first end-of-frame arrives before Update has run - and the
	/// one on which a pass ends and the next begins. Recording those would shift every
	/// subsequent row by one and skip plan frame 0.
	/// </summary>
	IEnumerator EndOfFrameLoop()
	{
		while (IsRunning)
		{
			yield return waitForEndOfFrame;

			if (!IsRunning) { yield break; }
			if (!frameStateWritten) { continue; }
			if (Records == null || FrameCursor >= Records.Length) { continue; }

			Transform camT = sceneRefs.camera.transform;

			// Preserve the sample already written by the next Update's Capture; only the
			// observed fields are filled here.
			FrameRecord record = Records[FrameCursor];
			record.cameraPosition = camT.position;
			record.cameraForward = camT.forward;
			record.deltaMs = Time.deltaTime * 1000.0;
			record.lodHighResCount = sceneRefs.lodSystem != null ? sceneRefs.lodSystem.HighResCount : 0;

			if (sceneRefs.solarSystem != null && sceneRefs.solarSystem.sun != null)
			{
				record.sunDirection = -sceneRefs.solarSystem.sun.transform.forward;
			}

			// captureDeltaTime pins Time.deltaTime (not unscaledDeltaTime - measured).
			// This is the direct check that the frame lock actually engaged.
			if (Mathf.Abs((float)(record.deltaMs - expectedDeltaMs)) > 0.01f)
			{
				deltaDriftFrames++;
			}

			Records[FrameCursor] = record;

			// After the record is complete, so a failed capture cannot cost us the frame's
			// data, and while FrameCursor still names this frame. The readback stalls here
			// for milliseconds - harmless, because a capture run reports no timings, and
			// captureDeltaTime means the stall does not advance simulated time either.
			if (Screenshots != null && Plan.frames[FrameCursor].screenshot)
			{
				PlannedFrame planned = Plan.frames[FrameCursor];
				string label = planned.segmentIndex >= 0
					&& planned.segmentIndex < Plan.segmentLabels.Length
					? Plan.segmentLabels[planned.segmentIndex] : "";

				Screenshots.CaptureNow(FrameCursor, passPlans[currentPass].passId, planned, label,
					record.cameraPosition, record.sunDirection, PlanetCentre());
			}

			frameStateWritten = false;
			FrameCursor++;
		}
	}

	void EndPass()
	{
		PassPlan pass = passPlans[currentPass];
		ObservedPoseHash = ComputePoseHash();

		if (deltaDriftFrames > 0) { AddWarning($"DELTA_TIME_DRIFT:{deltaDriftFrames}"); }

		// Snapshot the instrumentation state before disposing the sampler - the writer
		// needs it, and an absent counter must be recorded as absent rather than zero.
		if (Sampler != null)
		{
			FrameTimingAvailable = Sampler.FrameTimingAvailable;
			TimingLagFrames = Sampler.TimingLagFrames;
			AttributionAnomalies = Sampler.AttributionAnomalies;
			CounterAvailability = Sampler.CounterAvailability();

			if (!FrameTimingAvailable) { AddWarning("NO_GPU_TIME"); }
			if (AttributionAnomalies > 0)
			{
				AddWarning($"TIMING_ATTRIBUTION_ANOMALIES:{AttributionAnomalies}");
			}
		}

		if (logProgress)
		{
			var ci = CultureInfo.InvariantCulture;
			Debug.Log(
				$"[Benchmark] pass '{pass.passId}' complete\n" +
				$"  frames rendered   {FrameCursor} / {Plan.Length} " +
				$"{(FrameCursor == Plan.Length ? "(exact)" : "MISMATCH")}\n" +
				$"  plan_hash         0x{Plan.planHash:x16}\n" +
				$"  pose_hash         0x{ObservedPoseHash:x16}\n" +
				$"  delta drift       {deltaDriftFrames} frames " +
				$"(expected {expectedDeltaMs.ToString("F4", ci)} ms/frame)\n" +
				$"  gpu timing        {(FrameTimingAvailable ? $"available, lag {TimingLagFrames} frame(s)" : "UNAVAILABLE")}",
				this);
		}

		if (writeResults && !string.IsNullOrEmpty(runFolder))
		{
			string settings = pass.profile != null
				? pass.profile.DescribeSettings(sceneRefs)
				: "scene as authored";

			passResults.Add(BenchmarkWriter.WritePass(this, runFolder, pass.passId,
				pass.profile != null ? pass.profile.id : "asis", settings, pass.repeat));
		}

		// Undo this profile before the next one applies.
		passScope?.Dispose();
		passScope = null;
		Sampler?.Dispose();
		Sampler = null;

		BeginNextPass();
	}

	void FinishRun()
	{
		IsRunning = false;

		if (endOfFrameRoutine != null)
		{
			StopCoroutine(endOfFrameRoutine);
			endOfFrameRoutine = null;
		}

		// Before the summary: a capture failure has to be in warnings[] by the time run.json
		// is serialised, or the run would claim a clean sheet it does not have.
		FinaliseScreenshots();

		if (writeResults && !string.IsNullOrEmpty(runFolder) && passResults.Count > 0)
		{
			BenchmarkWriter.WriteRunSummary(this, runFolder, passResults, machineLabel);
			Debug.Log($"[Benchmark] run complete, {passResults.Count} pass(es) written to {runFolder}", this);
		}

		ReleaseResources();
		onCompleted?.Invoke(this);
	}

	[ContextMenu("Abort Run")]
	public void Abort()
	{
		if (!IsRunning) { return; }

		Debug.LogWarning($"[Benchmark] aborted at frame {FrameCursor} / {Plan.Length}.", this);
		IsRunning = false;
		ReleaseResources();
	}

	/// <summary>
	/// Every exit path funnels through here. The restore scope in particular must not
	/// outlive the run under any circumstance: the effect enabled flags it snapshotted
	/// are asset state, and a leaked change reaches disk on the next project save.
	/// </summary>
	void ReleaseResources()
	{
		if (endOfFrameRoutine != null)
		{
			StopCoroutine(endOfFrameRoutine);
			endOfFrameRoutine = null;
		}

		// Idempotent, and reached on the abort path too - an aborted capture should still
		// leave a manifest for the images that did land, otherwise the folder is a pile of
		// PNGs with no record of which frame produced which.
		FinaliseScreenshots();

		// Pass scope first: it was applied on top of the run scope, so it unwinds first.
		// Missing this on an abort would leave a profile's effect flags modified, and
		// those are asset state that reaches disk on the next project save.
		passScope?.Dispose();
		passScope = null;
		scope?.Dispose();
		scope = null;
		Sampler?.Dispose();
		Sampler = null;
	}

	/// <summary>Writes the manifest and folds any capture failures into warnings. Safe to
	/// call more than once.</summary>
	void FinaliseScreenshots()
	{
		if (Screenshots == null) { return; }

		Screenshots.WriteManifest();

		if (Screenshots.FailureCount > 0)
		{
			AddWarning($"SCREENSHOT_FAILURES:{Screenshots.FailureCount}");
		}

		Debug.Log($"[Benchmark] captured {Screenshots.Count} screenshot(s) to " +
			$"{Screenshots.Folder}", this);
		ScreenshotsCaptured = Screenshots.Count;
		Screenshots = null;
	}

	/// <summary>World-space centre of the planet, for the observer's local up vector. The
	/// earth orbits, so this is not the origin.</summary>
	Vector3 PlanetCentre()
	{
		return sceneRefs.Earth != null ? sceneRefs.Earth.transform.position : Vector3.zero;
	}

	void AddWarning(string warning)
	{
		// Passes replay the same plan, so a per-pass condition would otherwise be listed
		// once per pass.
		if (!warnings.Contains(warning)) { warnings.Add(warning); }
	}

	ulong ComputePoseHash()
	{
		ulong hash = 0xcbf29ce484222325UL;
		int count = Mathf.Min(FrameCursor, Records.Length);

		for (int i = 0; i < count; i++)
		{
			FrameRecord o = Records[i];
			MixQuantized(ref hash, o.cameraPosition.x, 10000f);
			MixQuantized(ref hash, o.cameraPosition.y, 10000f);
			MixQuantized(ref hash, o.cameraPosition.z, 10000f);
			MixQuantized(ref hash, o.cameraForward.x, 10000f);
			MixQuantized(ref hash, o.cameraForward.y, 10000f);
			MixQuantized(ref hash, o.cameraForward.z, 10000f);
			MixQuantized(ref hash, o.sunDirection.x, 10000f);
			MixQuantized(ref hash, o.sunDirection.y, 10000f);
			MixQuantized(ref hash, o.sunDirection.z, 10000f);
		}
		return hash;
	}

	static void MixQuantized(ref ulong hash, float value, float scale)
	{
		ulong q = unchecked((ulong)(long)Mathf.Round(value * scale));
		for (int shift = 0; shift < 64; shift += 8)
		{
			hash ^= (q >> shift) & 0xFF;
			hash *= 0x100000001b3UL;
		}
	}

	void OnDisable() { IsRunning = false; ReleaseResources(); }
	void OnDestroy() { ReleaseResources(); }
	void OnApplicationQuit() { IsRunning = false; ReleaseResources(); }

	[ContextMenu("Dump Plan CSV To Console")]
	void DumpPlan()
	{
		if (!sceneRefs.Resolve(out string error)) { Debug.LogError(error, this); return; }

		float aspect = targetResolution.y > 0 ? (float)targetResolution.x / targetResolution.y : 1.78f;
		BenchmarkPlan plan = BenchmarkPlan.Build(benchmark, sceneRefs.Earth,
			sceneRefs.testbedCamera.SurfaceRadius, aspect, fallbackBookmarks);

		Debug.Log(plan.Describe(), this);
		Debug.Log(plan.ToCsv(), this);
	}
}
