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
[DefaultExecutionOrder(-500)]
public class BenchmarkRunner : MonoBehaviour
{
	[Header("What to run")]
	public BenchmarkDefinition benchmark;
	public CameraBookmarks fallbackBookmarks;
	public BenchmarkSceneRefs sceneRefs = new BenchmarkSceneRefs();

	[Header("Setup")]
	public Vector2Int targetResolution = new Vector2Int(1920, 1080);
	public bool pinResolution = true;
	public bool runOnStart;

	[Header("Output")]
	public bool writeResults = true;
	// Identifies the pass within a run folder. Becomes "<profile>_r<repeat>" once renderer
	// profiles exist.
	public string passId = "default";
	// Free-text note recorded in run.json - which machine these numbers came from.
	public string machineLabel = "";
	// Leave empty for <project>/Results in the editor, <exe>/BenchmarkResults in a build.
	public string outputRootOverride = "";

	[Header("Debug")]
	public bool logProgress = true;

	public bool IsRunning { get; private set; }
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

	RestoreScope scope;
	readonly List<string> warnings = new List<string>();
	WaitForEndOfFrame waitForEndOfFrame;
	Coroutine endOfFrameRoutine;
	int deltaDriftFrames;
	double expectedDeltaMs;

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

		Records = new FrameRecord[Plan.Length];
		Sampler = new FrameSampler();
		FrameCursor = 0;
		deltaDriftFrames = 0;
		expectedDeltaMs = Plan.CaptureDeltaTime * 1000.0;
		IsRunning = true;

		waitForEndOfFrame = new WaitForEndOfFrame();
		endOfFrameRoutine = StartCoroutine(EndOfFrameLoop());

		Debug.Log($"[Benchmark] {Plan.Describe()}\n  warnings: " +
			(warnings.Count > 0 ? string.Join(", ", warnings) : "none"), this);
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
			Finish();
			return;
		}

		PlannedFrame frame = Plan.frames[FrameCursor];

		// Sun first: SolarSystemManager reads these at order 0, just after us.
		sceneRefs.solarSystem.SetTimes(frame.dayT, frame.monthT, frame.yearT);
		// SetView applies the transform synchronously, so onPreCull this frame sees it.
		sceneRefs.testbedCamera.SetView(frame.view);
	}

	/// <summary>
	/// Reads back what actually happened, after rendering. The pose hash is built from the
	/// observed transform rather than the planned one - otherwise it would only prove the
	/// plan equals itself, and would miss a stray input or a camera regression.
	/// </summary>
	IEnumerator EndOfFrameLoop()
	{
		while (IsRunning)
		{
			yield return waitForEndOfFrame;

			if (!IsRunning || FrameCursor >= Plan.Length) { yield break; }

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
			FrameCursor++;
		}
	}

	void Finish()
	{
		IsRunning = false;

		if (endOfFrameRoutine != null)
		{
			StopCoroutine(endOfFrameRoutine);
			endOfFrameRoutine = null;
		}

		ObservedPoseHash = ComputePoseHash();

		if (deltaDriftFrames > 0) { warnings.Add($"DELTA_TIME_DRIFT:{deltaDriftFrames}"); }

		// Snapshot the instrumentation state before disposing the sampler - the writer
		// needs it, and an absent counter must be recorded as absent rather than zero.
		if (Sampler != null)
		{
			FrameTimingAvailable = Sampler.FrameTimingAvailable;
			TimingLagFrames = Sampler.TimingLagFrames;
			AttributionAnomalies = Sampler.AttributionAnomalies;
			CounterAvailability = Sampler.CounterAvailability();

			if (!FrameTimingAvailable) { warnings.Add("NO_GPU_TIME"); }
			if (AttributionAnomalies > 0)
			{
				warnings.Add($"TIMING_ATTRIBUTION_ANOMALIES:{AttributionAnomalies}");
			}
		}

		ReleaseResources();

		if (logProgress)
		{
			var ci = CultureInfo.InvariantCulture;
			Debug.Log(
				$"[Benchmark] complete\n" +
				$"  frames rendered   {FrameCursor} / {Plan.Length} " +
				$"{(FrameCursor == Plan.Length ? "(exact)" : "MISMATCH")}\n" +
				$"  plan_hash         0x{Plan.planHash:x16}\n" +
				$"  pose_hash         0x{ObservedPoseHash:x16}\n" +
				$"  delta drift       {deltaDriftFrames} frames " +
				$"(expected {expectedDeltaMs.ToString("F4", ci)} ms/frame)\n" +
				$"  gpu timing        {(FrameTimingAvailable ? $"available, lag {TimingLagFrames} frame(s)" : "UNAVAILABLE")}\n" +
				$"  warnings          {(warnings.Count > 0 ? string.Join(", ", warnings) : "none")}\n" +
				$"  Run twice and compare pose_hash: equal means both runs rendered the same poses.",
				this);
		}

		if (writeResults)
		{
			string root = string.IsNullOrEmpty(outputRootOverride)
				? BenchmarkWriter.DefaultOutputRoot()
				: outputRootOverride;

			string folder = BenchmarkWriter.Write(this, root, passId, machineLabel);
			if (!string.IsNullOrEmpty(folder))
			{
				Debug.Log($"[Benchmark] results written to {folder}", this);
			}
		}

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

		scope?.Dispose();
		scope = null;
		Sampler?.Dispose();
		Sampler = null;
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
