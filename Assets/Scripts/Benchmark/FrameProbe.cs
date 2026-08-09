using System.Globalization;
using System.Text;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// TEMPORARY diagnostic. Answers the questions the benchmark harness is built on, before
/// any of it is built. Delete once its findings are recorded in NOTES.md.
///
/// Five questions, in order of how much damage a wrong assumption would do:
///
///   1. Does Time.captureDeltaTime THROTTLE, or does it only fix the timestep? The whole
///      frame-locked design assumes the latter. If it sleeps to hit the target rate, the
///      harness would be measuring a sleep rather than a renderer, and every number would
///      be wrong in a way that looks entirely plausible.
///   2. Is gpuFrameTime actually populated, and is it plausible? Requires Frame Timing
///      Stats to be enabled in Player Settings - without it every field reads zero.
///   3. How many frames of lag before a timing arrives, and does exactly one arrive per
///      frame? Exact per-frame attribution depends on the latter.
///   4. What fields does FrameTiming expose in this Unity version? Reported by reflection
///      rather than assumed, since the alignment strategy depends on whether a usable
///      identity/timestamp field exists.
///   5. Which ProfilerRecorder counters are valid here? Unity strips much of the profiler
///      from non-development players.
/// </summary>
public class FrameProbe : MonoBehaviour
{
	[Header("Run")]
	public bool runOnStart;
	public int warmupFrames = 120;
	public int measureFrames = 600;

	[Header("Frame lock")]
	public bool applyCaptureDeltaTime = true;
	public float simulatedFps = 60f;

	const int TimingWindow = 16;

	enum Phase { Idle, Warmup, Measure, Report }

	Phase phase = Phase.Idle;
	int phaseFrame;

	System.Diagnostics.Stopwatch stopwatch;
	long previousTicks;

	// Wall clock
	double wallTotalMs;
	double wallMinMs, wallMaxMs;

	// Frame timings
	FrameTiming[] timingBuffer;
	double cpuTotalMs, gpuTotalMs;
	int timingsSeen;
	int framesUntilFirstTiming = -1;
	int captureFrameCount;
	int framesWithZeroNewTimings, framesWithOneNewTiming, framesWithManyNewTimings;
	double lastSeenPresentTime = -1;
	bool anyNonZeroGpu;

	// Delta time stability. Both clocks, because captureDeltaTime pins Time.deltaTime but
	// NOT Time.unscaledDeltaTime - measured, not assumed.
	double deltaMinMs = double.MaxValue, deltaMaxMs;
	double unscaledMinMs = double.MaxValue, unscaledMaxMs;

	// Counters
	ProfilerRecorder[] recorders;
	string[] recorderNames;
	ProfilerCategory[] recorderCategories;

	static readonly (string category, string name)[] CounterProbes =
	{
		("Render", "Draw Calls Count"),
		("Render", "Batches Count"),
		("Render", "SetPass Calls Count"),
		("Render", "Triangles Count"),
		("Render", "Vertices Count"),
		("Render", "Shadow Casters Count"),
		("Memory", "Total Used Memory"),
		("Memory", "Total Reserved Memory"),
		("Memory", "GC Used Memory"),
		("Memory", "GC Reserved Memory"),
		("Memory", "Gfx Used Memory"),
		("Memory", "System Used Memory"),
		("Memory", "GC Allocated In Frame"),
	};

	float restoreCaptureDeltaTime;
	int restoreVSync;
	int restoreTargetFrameRate;

	void Start()
	{
		if (runOnStart) { Begin(); }
	}

	[ContextMenu("Run Probe")]
	public void Begin()
	{
		if (phase != Phase.Idle)
		{
			Debug.LogWarning("[Probe] already running.", this);
			return;
		}

		ReportEnvironment();
		ReportFrameTimingFields();
		OpenRecorders();

		restoreCaptureDeltaTime = Time.captureDeltaTime;
		restoreVSync = QualitySettings.vSyncCount;
		restoreTargetFrameRate = Application.targetFrameRate;

		QualitySettings.vSyncCount = 0;
		Application.targetFrameRate = -1;
		if (applyCaptureDeltaTime) { Time.captureDeltaTime = 1f / Mathf.Max(1f, simulatedFps); }

		timingBuffer = new FrameTiming[TimingWindow];
		stopwatch = System.Diagnostics.Stopwatch.StartNew();
		previousTicks = stopwatch.ElapsedTicks;

		wallTotalMs = 0; cpuTotalMs = 0; gpuTotalMs = 0;
		wallMinMs = double.MaxValue; wallMaxMs = 0;
		deltaMinMs = double.MaxValue; deltaMaxMs = 0;
		timingsSeen = 0; captureFrameCount = 0; framesUntilFirstTiming = -1;
		framesWithZeroNewTimings = framesWithOneNewTiming = framesWithManyNewTimings = 0;
		lastSeenPresentTime = -1; anyNonZeroGpu = false;

		phase = Phase.Warmup;
		phaseFrame = 0;
		Debug.Log($"[Probe] started: {warmupFrames} warm-up + {measureFrames} measured frames, " +
			$"captureDeltaTime {(applyCaptureDeltaTime ? (1f / simulatedFps).ToString("F5", CultureInfo.InvariantCulture) : "OFF")}", this);
	}

	void Update()
	{
		if (phase == Phase.Idle || phase == Phase.Report) { return; }

		long ticks = stopwatch.ElapsedTicks;
		double wallMs = (ticks - previousTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
		previousTicks = ticks;

		FrameTimingManager.CaptureFrameTimings();
		captureFrameCount++;
		int newTimings = DrainTimings(out double cpuMs, out double gpuMs);

		double deltaMs = Time.deltaTime * 1000.0;
		double unscaledMs = Time.unscaledDeltaTime * 1000.0;

		if (phase == Phase.Measure)
		{
			wallTotalMs += wallMs;
			if (wallMs < wallMinMs) { wallMinMs = wallMs; }
			if (wallMs > wallMaxMs) { wallMaxMs = wallMs; }

			if (deltaMs < deltaMinMs) { deltaMinMs = deltaMs; }
			if (deltaMs > deltaMaxMs) { deltaMaxMs = deltaMs; }
			if (unscaledMs < unscaledMinMs) { unscaledMinMs = unscaledMs; }
			if (unscaledMs > unscaledMaxMs) { unscaledMaxMs = unscaledMs; }

			cpuTotalMs += cpuMs;
			gpuTotalMs += gpuMs;
			timingsSeen += newTimings;

			if (newTimings == 0) { framesWithZeroNewTimings++; }
			else if (newTimings == 1) { framesWithOneNewTiming++; }
			else { framesWithManyNewTimings++; }
		}

		phaseFrame++;

		if (phase == Phase.Warmup && phaseFrame >= warmupFrames)
		{
			phase = Phase.Measure;
			phaseFrame = 0;
			// Reset wall accounting so warm-up cost is excluded.
			previousTicks = stopwatch.ElapsedTicks;
		}
		else if (phase == Phase.Measure && phaseFrame >= measureFrames)
		{
			phase = Phase.Report;
			Report();
			Restore();
		}
	}

	/// <summary>
	/// Pulls timings newer than the last one seen. Returns how many arrived this frame,
	/// which is the number that decides whether exact per-frame attribution is possible.
	/// </summary>
	int DrainTimings(out double cpuMs, out double gpuMs)
	{
		cpuMs = 0; gpuMs = 0;

		uint count = FrameTimingManager.GetLatestTimings(TimingWindow, timingBuffer);
		if (count == 0) { return 0; }

		// Buffer is newest-first. Walk until we reach one already seen.
		int fresh = 0;
		for (int i = 0; i < count; i++)
		{
			double key = timingBuffer[i].cpuTimePresentCalled;
			if (lastSeenPresentTime >= 0 && key <= lastSeenPresentTime) { break; }
			fresh++;
		}

		if (fresh > 0)
		{
			lastSeenPresentTime = timingBuffer[0].cpuTimePresentCalled;

			for (int i = 0; i < fresh; i++)
			{
				cpuMs += timingBuffer[i].cpuFrameTime;
				gpuMs += timingBuffer[i].gpuFrameTime;
				if (timingBuffer[i].gpuFrameTime > 0) { anyNonZeroGpu = true; }
			}

			if (framesUntilFirstTiming < 0) { framesUntilFirstTiming = captureFrameCount; }
		}

		return fresh;
	}

	// ------------------------------------------------------------------ reporting

	void ReportEnvironment()
	{
		Debug.Log(
			"[Probe] environment\n" +
			$"   unity            {Application.unityVersion}\n" +
			$"   editor           {Application.isEditor}   development build {Debug.isDebugBuild}\n" +
			$"   graphics         {SystemInfo.graphicsDeviceType} / {SystemInfo.graphicsDeviceName}\n" +
			$"   colour space     {QualitySettings.activeColorSpace}\n" +
			$"   vSyncCount       {QualitySettings.vSyncCount} (0 required)\n" +
			$"   targetFrameRate  {Application.targetFrameRate}\n" +
			$"   MSAA             {QualitySettings.antiAliasing}\n" +
			$"   screen           {Screen.width}x{Screen.height}  fullScreen {Screen.fullScreenMode}\n" +
			$"   runInBackground  {Application.runInBackground}", this);
	}

	/// <summary>
	/// Enumerates FrameTiming's fields by reflection instead of assuming the shape. The
	/// alignment strategy depends on whether a usable identity field exists, and that is
	/// cheaper to look up than to guess wrong.
	/// </summary>
	void ReportFrameTimingFields()
	{
		var sb = new StringBuilder("[Probe] FrameTiming fields in this Unity version:\n");
		foreach (var field in typeof(FrameTiming).GetFields())
		{
			sb.Append("   ").Append(field.FieldType.Name).Append(' ').Append(field.Name).Append('\n');
		}
		Debug.Log(sb.ToString(), this);
	}

	void OpenRecorders()
	{
		recorders = new ProfilerRecorder[CounterProbes.Length];
		recorderNames = new string[CounterProbes.Length];
		recorderCategories = new ProfilerCategory[CounterProbes.Length];

		for (int i = 0; i < CounterProbes.Length; i++)
		{
			ProfilerCategory category = CounterProbes[i].category == "Render"
				? ProfilerCategory.Render
				: ProfilerCategory.Memory;

			recorderCategories[i] = category;
			recorderNames[i] = CounterProbes[i].name;
			recorders[i] = ProfilerRecorder.StartNew(category, CounterProbes[i].name);
		}
	}

	void Report()
	{
		int n = measureFrames;
		double meanWall = wallTotalMs / n;
		double expectedIfThrottled = 1000.0 / simulatedFps;
		double meanCpu = timingsSeen > 0 ? cpuTotalMs / timingsSeen : 0;
		double meanGpu = timingsSeen > 0 ? gpuTotalMs / timingsSeen : 0;

		var sb = new StringBuilder();
		sb.Append("[Probe] RESULTS over ").Append(n).Append(" measured frames\n\n");

		// --- Q1: does captureDeltaTime throttle? ---
		sb.Append("Q1  captureDeltaTime throttling\n");
		sb.Append(F("      mean wall ms/frame", meanWall));
		sb.Append(F("      if throttled, would be", expectedIfThrottled));
		sb.Append(F("      mean cpuFrameTime ms", meanCpu));
		if (!applyCaptureDeltaTime)
		{
			sb.Append("      -> captureDeltaTime was OFF for this run; rerun with it on to compare\n");
		}
		else
		{
			bool looksThrottled = meanWall > expectedIfThrottled * 0.9;
			sb.Append(looksThrottled
				? "      -> THROTTLING SUSPECTED. Wall time tracks the simulated rate, not the\n" +
				  "         renderer. The frame-locked design cannot use captureDeltaTime as-is.\n"
				: "      -> OK. Wall time is independent of the simulated rate; captureDeltaTime\n" +
				  "         fixes the timestep without sleeping.\n");
		}
		sb.Append(F("      min wall ms", wallMinMs));
		sb.Append(F("      max wall ms", wallMaxMs));
		sb.Append('\n');

		// --- delta time stability ---
		sb.Append("    which clock does captureDeltaTime pin?\n");
		sb.Append(F("      Time.deltaTime min ms", deltaMinMs));
		sb.Append(F("      Time.deltaTime max ms", deltaMaxMs));
		sb.Append(F("      Time.unscaledDeltaTime min ms", unscaledMinMs));
		sb.Append(F("      Time.unscaledDeltaTime max ms", unscaledMaxMs));
		sb.Append(F("      expected if pinned ms", applyCaptureDeltaTime ? expectedIfThrottled : double.NaN));
		sb.Append("      -> the harness must assert frame lock on the PINNED clock; the other\n" +
			"         one still reports real elapsed time.\n");
		sb.Append('\n');

		// --- Q2/Q3: frame timings ---
		sb.Append("Q2  GPU timing availability\n");
		if (!anyNonZeroGpu)
		{
			sb.Append("      -> NO GPU TIME. Every gpuFrameTime read zero.\n");
			sb.Append("         Enable Player Settings > Other Settings > Frame Timing Stats.\n");
		}
		else
		{
			sb.Append(F("      mean gpuFrameTime ms", meanGpu));
			sb.Append(F("      mean cpuFrameTime ms", meanCpu));
			sb.Append("      -> populated and plausible if these are the same order as wall time.\n");
		}
		sb.Append('\n');

		sb.Append("Q3  timing lag and attribution\n");
		sb.Append("      frames before first timing   ").Append(framesUntilFirstTiming).Append('\n');
		sb.Append("      timings received             ").Append(timingsSeen).Append(" over ").Append(n).Append(" frames\n");
		sb.Append("      frames with 0 new timings    ").Append(framesWithZeroNewTimings).Append('\n');
		sb.Append("      frames with 1 new timing     ").Append(framesWithOneNewTiming).Append('\n');
		sb.Append("      frames with >1 new timings   ").Append(framesWithManyNewTimings).Append('\n');
		bool exact = framesWithOneNewTiming >= n - 2 && framesWithManyNewTimings == 0;
		sb.Append(exact
			? "      -> EXACT per-frame attribution is possible (one timing per frame).\n"
			: "      -> NOT one-per-frame. Fall back to segment-level aggregation and record\n" +
			  "         timing_alignment: approximate.\n");
		sb.Append('\n');

		// --- Q5: counters ---
		sb.Append("Q5  ProfilerRecorder availability\n");
		for (int i = 0; i < recorders.Length; i++)
		{
			bool valid = recorders[i].Valid;
			sb.Append("      ").Append(valid ? "OK    " : "ABSENT")
			  .Append("  ").Append(recorderCategories[i].Name).Append(" / ").Append(recorderNames[i]);
			if (valid) { sb.Append("   last = ").Append(recorders[i].LastValue); }
			sb.Append('\n');
		}

		Debug.Log(sb.ToString(), this);
	}

	static string F(string label, double value)
	{
		return label + "  " + value.ToString("F4", CultureInfo.InvariantCulture) + "\n";
	}

	void Restore()
	{
		Time.captureDeltaTime = restoreCaptureDeltaTime;
		QualitySettings.vSyncCount = restoreVSync;
		Application.targetFrameRate = restoreTargetFrameRate;
		CloseRecorders();
		Debug.Log("[Probe] finished, settings restored.", this);
	}

	void CloseRecorders()
	{
		if (recorders == null) { return; }
		for (int i = 0; i < recorders.Length; i++)
		{
			if (recorders[i].Valid) { recorders[i].Dispose(); }
		}
		recorders = null;
	}

	void OnDisable()
	{
		if (phase != Phase.Idle && phase != Phase.Report) { Restore(); }
		else { CloseRecorders(); }
		phase = Phase.Idle;
	}
}
