using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
// For SolarTime, which formats the clock the merged overlay shows.
using SolarSystem;

/// <summary>
/// In-application benchmark control: pick a benchmark and a mode with the keyboard, then
/// run without leaving the player.
///
/// Complements the command line rather than replacing it. Scripted runs want
/// `-benchmark ... -quitWhenDone`; driving it by hand wants to see what is selected and
/// press a key. Both end up in the same <see cref="BenchmarkRunner"/>.
///
/// The overlay hides itself while a run is in progress. IMGUI draws into the same
/// backbuffer everything else does, so it would appear in every captured screenshot and add
/// its own draw calls to the counters being recorded. Progress goes to the console instead.
/// </summary>
public class BenchmarkHud : MonoBehaviour
{
	static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

	[Header("Wiring")]
	public BenchmarkRunner runner;

	[Tooltip("Drawn by this overlay rather than its own. One panel and one hide key beat two " +
		"stacked in opposite corners, each with a separate toggle - which is how the UI ended up " +
		"impossible to fully dismiss.")]
	public TimeController time;

	[Header("Keys")]
	// F1-F8, ordered by how often they are reached for rather than by which system owns them.
	// The old layout grew one key at a time - cycle-benchmark on F2 because it was written first,
	// hide split across F1 and F6 because two components each drew their own panel - and it read as
	// an implementation history rather than as a control surface.
	public KeyCode cycleProfileKey = KeyCode.F1;
	// F2 is the world scale, owned by WorldScaleController.
	public KeyCode cycleGenusKey = KeyCode.F3;
	public KeyCode cycleModeKey = KeyCode.F4;
	public KeyCode runKey = KeyCode.F5;
	public KeyCode cycleBenchmarkKey = KeyCode.F6;
	public KeyCode toggleOverlayKey = KeyCode.F7;
	public KeyCode fullscreenKey = KeyCode.F8;
	public KeyCode cycleResolutionKey = KeyCode.F9;
	public KeyCode togglePerformanceKey = KeyCode.F10;
	public KeyCode abortKey = KeyCode.Escape;

	[Tooltip("Cycled by the resolution key. These drive the runner's targetResolution, so the " +
		"resolution you looked at and the one a run pins are the same value.")]
	public Vector2Int[] resolutions =
	{
		new Vector2Int(1920, 1080),
		new Vector2Int(2560, 1440),
		new Vector2Int(3840, 2160),
	};

	[Header("Overlay")]
	public bool showOverlay = true;
	public int overlayFontSize = 17;
	public Color overlayTextColour = Color.white;
	public Color overlayBackgroundColour = new Color(0f, 0f, 0f, 0.72f);

	[Tooltip("Draw a progress line while a run is in progress. Off by default: IMGUI adds " +
		"draw calls and CPU time to the frames being measured. Never drawn during a capture " +
		"run regardless, since it would appear in the images.")]
	public bool showProgressDuringRun;

	/// <summary>Index into availableBenchmarks, or AllIndex for "run every one in turn".</summary>
	int selected;
	int AllIndex => Available.Count;

	readonly List<BenchmarkDefinition> queue = new List<BenchmarkDefinition>();
	bool advanceQueue;

	// Batch state, used only when All is selected. Each benchmark still writes its own run
	// folder - they have different plans and frame counts - but they land inside one parent
	// with a cross-benchmark summary written when the queue drains.
	readonly List<BenchmarkWriter.BatchEntry> batchEntries = new List<BenchmarkWriter.BatchEntry>();
	string batchFolder;
	string savedOutputOverride;
	int queueLengthAtStart;

	// Live profile preview. -1 is "scene as authored".
	//
	// The scope is not optional bookkeeping: applying a profile writes
	// PostProcessingEffect.enabled, which is a serialized field on a ScriptableObject asset.
	// Leaving a preview applied would write it to the project on the next save, so the scene
	// would quietly come back configured as whichever profile was last looked at.
	int previewIndex = -1;
	RestoreScope previewScope;

	GUIStyle overlayStyle;
	Texture2D overlayBackground;
	Color appliedBackgroundColour;

	// ------------------------------------------------------------------ performance panel
	//
	// Off by default. It is a diagnostic, and IMGUI is not free - the same reason the main panel
	// hides itself for the duration of a run.
	[Tooltip("Live performance readout, top right. Off by default: IMGUI costs frame time, which " +
		"is the thing it is reporting.")]
	public bool showPerformance;

	/// <summary>
	/// Reuses the harness's own sampler rather than reading Time.deltaTime.
	///
	/// It is the difference between "how long did the frame take" and "where did the frame go":
	/// FrameTimingManager gives real GPU time, and the profiler recorders give memory and draw
	/// calls. Created lazily, because opening the recorders is not free and most sessions never
	/// show this panel.
	/// </summary>
	FrameSampler perfSampler;

	System.Diagnostics.Process perfProcess;
	double perfLastCpuSeconds = -1;
	float perfLastCpuSampleTime;
	float perfCpuPercent = -1;

	// Accumulated between refreshes. Per-frame values flicker far too fast to read, so the panel
	// shows a mean over a short window rather than the last frame.
	double perfWallSum, perfCpuMainSum, perfCpuRenderSum, perfGpuSum;
	int perfSampleCount;
	float perfNextRefresh;
	string perfText = "";

	List<BenchmarkDefinition> Available
	{
		get
		{
			available.Clear();
			if (runner != null && runner.availableBenchmarks != null)
			{
				foreach (BenchmarkDefinition definition in runner.availableBenchmarks)
				{
					if (definition != null) { available.Add(definition); }
				}
			}
			return available;
		}
	}

	readonly List<BenchmarkDefinition> available = new List<BenchmarkDefinition>();

	void Awake()
	{
		if (runner == null) { runner = GetComponent<BenchmarkRunner>(); }
		if (runner == null) { runner = FindFirstObjectByType<BenchmarkRunner>(); }
		// Found rather than wired, so merging the two overlays costs no scene edit.
		if (time == null) { time = FindFirstObjectByType<TimeController>(); }

		// All, rather than whichever benchmark happens to sit first in the list. Running a single
		// one is the deliberate act; running the set is the default thing to want - and a default
		// that silently picks one of several is a default you have to check before every run.
		selected = AllIndex;
	}

	void OnEnable()
	{
		if (runner != null) { runner.onCompleted += HandleCompleted; }
	}

	void OnDisable()
	{
		if (runner != null) { runner.onCompleted -= HandleCompleted; }

		// Effect enabled flags are asset state. A preview left applied when play mode ends
		// would be written to the project on the next save.
		ClearPreview();

		// ProfilerRecorders are native handles; leaving them open across a domain reload leaks them.
		ReleasePerformanceSampler();
	}

	void Update()
	{
		if (runner == null) { return; }

		// Hides everything, including the performance panel: that one draws only when both this and
		// its own flag are set, so F7 hides all of it and restores whatever was showing.
		if (Input.GetKeyDown(toggleOverlayKey)) { showOverlay = !showOverlay; }

		// Sampled outside the IsRunning early-out below, but not DURING a run - the panel is hidden
		// then anyway, and its recorders would be reading frames the harness is measuring.
		if (showOverlay && showPerformance && !runner.IsRunning) { SamplePerformance(); }
		else if (!showPerformance) { ReleasePerformanceSampler(); }

		if (runner.IsRunning)
		{
			// Abort is the only input accepted mid-run: everything else would change the
			// configuration of a run already in progress.
			if (Input.GetKeyDown(abortKey))
			{
				queue.Clear();
				advanceQueue = false;
				runner.Abort();

				// Abort does not raise onCompleted, so the batch has to be closed here. The
				// aborted run wrote nothing, but earlier ones in the batch did - summarising
				// those is better than discarding them, as long as it is clear the batch is
				// short.
				if (batchFolder != null && queueLengthAtStart > batchEntries.Count)
				{
					Debug.LogWarning($"[Benchmark] batch aborted after {batchEntries.Count} of " +
						$"{queueLengthAtStart} benchmark(s); the summary covers only those.", this);
				}
				FinishBatch();
			}
			return;
		}

		// Deferred to the frame after completion rather than started from the callback, so
		// the finished run has fully released its environment scope before the next pins it.
		if (advanceQueue)
		{
			advanceQueue = false;
			StartNextInQueue();
			return;
		}

		if (Input.GetKeyDown(cycleBenchmarkKey)) { CycleBenchmark(); }
		if (Input.GetKeyDown(cycleModeKey)) { CycleMode(); }
		if (Input.GetKeyDown(cycleProfileKey)) { CyclePreviewProfile(); }
		if (Input.GetKeyDown(cycleGenusKey)) { CycleGenus(); }
		if (Input.GetKeyDown(runKey)) { Run(); }
		if (Input.GetKeyDown(fullscreenKey)) { ToggleFullscreen(); }
		if (Input.GetKeyDown(cycleResolutionKey)) { CycleResolution(); }
		if (Input.GetKeyDown(togglePerformanceKey)) { showPerformance = !showPerformance; }
	}

	// ------------------------------------------------------------------ live preview

	/// <summary>
	/// Applies the next renderer profile live, so the difference can be looked at rather than
	/// only measured. Not part of any measurement path - a benchmark applies its own profiles
	/// through its own scope.
	/// </summary>
	void CyclePreviewProfile()
	{
		List<RendererProfile> profiles = LivePreviewProfiles();
		if (profiles.Count == 0)
		{
			Debug.LogWarning("[Benchmark] no renderer profiles on the runner - point its Profiles " +
				"array at one of the generated sets.", this);
			return;
		}

		if (!runner.sceneRefs.Resolve(out string error))
		{
			Debug.LogWarning($"[Benchmark] {error}", this);
			return;
		}

		// Unwind the previous preview before applying the next, so profiles compose by
		// replacement rather than by accumulation.
		ClearPreview();

		// One past the end returns to the scene's authored configuration, which is the only
		// way to get back to it without restarting.
		previewIndex++;
		if (previewIndex >= profiles.Count)
		{
			previewIndex = -1;
			return;
		}

		RendererProfile profile = profiles[previewIndex];
		previewScope = new RestoreScope();
		profile.Apply(runner.sceneRefs, previewScope);
	}

	/// <summary>Undoes the live preview. Safe to call when nothing is previewed.</summary>
	void ClearPreview()
	{
		previewScope?.Dispose();
		previewScope = null;
	}

	List<RendererProfile> LivePreviewProfiles()
	{
		previewProfiles.Clear();
		if (runner.profiles != null)
		{
			foreach (RendererProfile profile in runner.profiles)
			{
				if (profile != null) { previewProfiles.Add(profile); }
			}
		}
		return previewProfiles;
	}

	readonly List<RendererProfile> previewProfiles = new List<RendererProfile>();

/// <summary>
	/// Steps the volumetric's genus override through the ten of THESIS.md 3.5, plus Off.
	///
	/// RQ1 needs one image of each, and hunting for a region where the noise happened to produce
	/// Cirrocumulus is not a workflow. Paired with the capture mode this makes the evidence set a
	/// couple of minutes of tapping.
	/// </summary>
	void CycleGenus()
	{
		Clouds.CloudEffect clouds = FindClouds();
		if (clouds == null)
		{
			Debug.LogWarning("[Benchmark] no CloudEffect in the post-processing chain, so there is " +
				"no genus override to cycle.", this);
			return;
		}

		var values = (Clouds.CloudEffect.Genus[])System.Enum.GetValues(typeof(Clouds.CloudEffect.Genus));
		int next = (System.Array.IndexOf(values, clouds.genusOverride) + 1) % values.Length;
		clouds.genusOverride = values[next];
	}

	Clouds.CloudEffect FindClouds()
	{
		if (runner == null || !runner.sceneRefs.Resolve(out _)) { return null; }
		if (runner.sceneRefs.postProcessing == null) { return null; }

		foreach (PostProcessingEffect effect in runner.sceneRefs.postProcessing.effects)
		{
			if (effect is Clouds.CloudEffect clouds) { return clouds; }
		}
		return null;
	}

	/// <summary>
	/// Borderless-window fullscreen rather than exclusive.
	///
	/// Exclusive fullscreen changes the swap chain and can change the frame pacing, which is the
	/// thing being measured - so a key that flips it mid-session would quietly invalidate a
	/// comparison across the flip. This mode does not.
	/// </summary>
	/// <summary>
	/// Steps the render resolution, and does it by setting the runner's targetResolution.
	///
	/// Not a separate Screen.SetResolution of its own: the runner pins that value at the start of
	/// every run, so a resolution set independently would be silently replaced the moment a
	/// benchmark started - and the images would then be at a different resolution from the one that
	/// was being looked at when the pose was chosen. Driving the same field is the same argument as
	/// the profile preview applying real profile assets.
	///
	/// BenchmarkWriter already compares the achieved resolution against this and records the
	/// mismatch, so a run at the wrong size reports itself rather than quietly counting.
	/// </summary>
	void CycleResolution()
	{
		if (resolutions == null || resolutions.Length == 0)
		{
			Debug.LogWarning("[Benchmark] no resolutions configured to cycle.", this);
			return;
		}

		int index = System.Array.IndexOf(resolutions, runner.targetResolution);
		runner.targetResolution = resolutions[(index + 1) % resolutions.Length];

		// The editor ignores Screen.SetResolution - the Game view size wins - which is the same
		// reason BenchmarkEnvironment skips pinning there and warns instead.
		if (Application.isEditor)
		{
			Debug.Log($"[Benchmark] target resolution {runner.targetResolution.x}x" +
				$"{runner.targetResolution.y}. The editor ignores it; the Game view size wins, so " +
				"this only takes effect in a player.", this);
			return;
		}

		Screen.SetResolution(runner.targetResolution.x, runner.targetResolution.y, Screen.fullScreenMode);
	}

	// ------------------------------------------------------------------ performance panel

	/// <summary>
	/// Accumulates a frame, and rebuilds the displayed text a few times a second.
	///
	/// Not once per frame: at 150 fps the numbers change faster than they can be read, and an
	/// average over a short window is also the more honest figure - a single frame's GPU time says
	/// as much about where the sample landed as about the workload.
	/// </summary>
	void SamplePerformance()
	{
		perfSampler ??= new FrameSampler();

		FrameSampler.Sample sample = perfSampler.Capture();
		perfWallSum += sample.wallMs;
		perfCpuMainSum += sample.cpuMainMs;
		perfCpuRenderSum += sample.cpuRenderMs;
		perfGpuSum += sample.gpuMs;
		perfSampleCount++;

		SampleProcessCpu();

		if (Time.unscaledTime < perfNextRefresh) { return; }
		perfNextRefresh = Time.unscaledTime + 0.25f;

		if (perfSampleCount == 0) { return; }

		double wall = perfWallSum / perfSampleCount;
		double gpu = perfGpuSum / perfSampleCount;

		string fps = wall > 0 ? (1000.0 / wall).ToString("0", Ci) : "-";
		string gpuLine = perfSampler.FrameTimingAvailable
			? $"{gpu.ToString("0.00", Ci)} ms"
			: "unavailable";

		perfText =
			$"fps         {fps}   ({wall.ToString("0.00", Ci)} ms)\n" +
			$"cpu main    {(perfCpuMainSum / perfSampleCount).ToString("0.00", Ci)} ms\n" +
			$"cpu render  {(perfCpuRenderSum / perfSampleCount).ToString("0.00", Ci)} ms\n" +
			$"gpu         {gpuLine}\n" +
			$"process cpu {(perfCpuPercent < 0 ? "n/a" : perfCpuPercent.ToString("0", Ci) + " %")}\n" +
			$"memory      {Mb(sample.totalUsedBytes)}   gfx {Mb(sample.gfxUsedBytes)}\n" +
			$"draw calls  {sample.drawCalls.ToString("N0", Ci)}";

		// Deliberately absent: CPU and GPU die temperature, and GPU utilisation. None is reachable
		// from Unity - they come from vendor interfaces (NVML, ADL) or Windows performance
		// counters - so showing them would mean either a permanent "n/a" or a vendor-locked
		// dependency. Worth knowing if the question comes up again: GPU temperature is the one with
		// a real bearing on the measurements, because thermal throttling mid-run would bias later
		// passes against earlier ones.

		perfWallSum = perfCpuMainSum = perfCpuRenderSum = perfGpuSum = 0;
		perfSampleCount = 0;
	}

	/// <summary>
	/// This process's CPU use, as a percentage of one core-second per wall second.
	///
	/// The process rather than the machine: system-wide load is not something Unity can see, and
	/// the renderer's own cost is the number that means anything here anyway. Divided by the core
	/// count so it reads 0-100 rather than 0-N*100.
	/// </summary>
	void SampleProcessCpu()
	{
		try
		{
			perfProcess ??= System.Diagnostics.Process.GetCurrentProcess();

			double cpuSeconds = perfProcess.TotalProcessorTime.TotalSeconds;
			float now = Time.realtimeSinceStartup;

			if (perfLastCpuSeconds >= 0 && now > perfLastCpuSampleTime)
			{
				double busy = cpuSeconds - perfLastCpuSeconds;
				double elapsed = now - perfLastCpuSampleTime;
				perfCpuPercent = (float)(100.0 * busy / (elapsed * Mathf.Max(1, SystemInfo.processorCount)));
			}

			perfLastCpuSeconds = cpuSeconds;
			perfLastCpuSampleTime = now;
		}
		catch (System.Exception)
		{
			// Restricted on some platforms. Reported as n/a rather than as a wrong number.
			perfCpuPercent = -1;
			perfProcess = null;
		}
	}

	static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("N0", Ci) + " MB";

	void ReleasePerformanceSampler()
	{
		if (perfSampler == null) { return; }
		perfSampler.Dispose();
		perfSampler = null;
		perfText = "";
		perfSampleCount = 0;
		perfWallSum = perfCpuMainSum = perfCpuRenderSum = perfGpuSum = 0;
	}

	void ToggleFullscreen()
	{
		// ONE assignment. Setting the mode and then toggling Screen.fullScreen fought itself:
		// assigning the mode already implies the flag, but the flag does not update until the end
		// of the frame, so the second line read the stale value and undid the first.
		bool goingFullscreen = Screen.fullScreenMode == FullScreenMode.Windowed;
		Screen.fullScreenMode = goingFullscreen
			? FullScreenMode.FullScreenWindow
			: FullScreenMode.Windowed;

		// Only the editor no-op is worth saying. Everything else this key does is visible in the
		// window itself, which is why nothing is echoed to the overlay.
		if (Application.isEditor)
		{
			Debug.Log("[Benchmark] fullscreen only takes effect in a player - the Game view " +
				"is not a window Unity can take fullscreen.", this);
		}
	}

	void CycleBenchmark()
	{
		int count = Available.Count;
		if (count == 0) { Debug.LogWarning("[Benchmark] no benchmarks in availableBenchmarks.", this); return; }

		// One past the end is "All", so the list always offers it even with a single entry.
		selected = (selected + 1) % (count + 1);
	}

	void CycleMode()
	{
		runner.mode = runner.mode switch
		{
			BenchmarkRunMode.Timing => BenchmarkRunMode.Capture,
			BenchmarkRunMode.Capture => BenchmarkRunMode.SelfCheck,
			_ => BenchmarkRunMode.Timing
		};
	}

	void Run()
	{
		List<BenchmarkDefinition> definitions = Available;
		if (definitions.Count == 0) { Debug.LogWarning("[Benchmark] no benchmarks in availableBenchmarks.", this); return; }

		// Before anything else. A run pins the environment by snapshotting current values, so
		// starting one with a preview applied would record the previewed state as the thing to
		// restore to - and every pass would measure from a perturbed starting point.
		ClearPreview();
		previewIndex = -1;

		queue.Clear();
		batchEntries.Clear();
		batchFolder = null;

		if (selected == AllIndex)
		{
			// Not every available benchmark - only the ones that claim to produce a result. The
			// smoke test exists to prove the harness runs end to end and says so itself; queuing it
			// here would spend time on it and leave a row in the batch summary that reads like a
			// measurement.
			foreach (BenchmarkDefinition definition in definitions)
			{
				if (definition.includeInAll) { queue.Add(definition); }
			}

			if (queue.Count == 0)
			{
				Debug.LogWarning("[Benchmark] every available benchmark is excluded from All.", this);
				return;
			}

			// One parent folder for the whole batch. Redirecting the runner's output root is
			// what puts each run inside it; the override is restored when the batch ends so
			// a later single run is unaffected.
			savedOutputOverride = runner.outputRootOverride;
			string root = string.IsNullOrEmpty(runner.outputRootOverride)
				? BenchmarkWriter.DefaultOutputRoot()
				: runner.outputRootOverride;

			batchFolder = BenchmarkWriter.BeginBatch(root);
			runner.outputRootOverride = batchFolder;
		}
		else
		{
			queue.Add(definitions[Mathf.Clamp(selected, 0, definitions.Count - 1)]);
		}

		queueLengthAtStart = queue.Count;
		StartNextInQueue();
	}

	void StartNextInQueue()
	{
		// Each queued benchmark is its own run with its own output folder - they have
		// different plans, so they could not share one.
		while (queue.Count > 0)
		{
			BenchmarkDefinition next = queue[0];
			queue.RemoveAt(0);

			runner.benchmark = next;
			runner.StartRun();

			if (runner.IsRunning) { return; }

			// StartRun refuses in some configurations - most likely a capture run over a
			// benchmark that marks no frames. Skip it and continue rather than stalling the
			// whole queue on one bad entry.
			Debug.LogWarning($"[Benchmark] '{next.id}' did not start; skipping to the next " +
				"queued benchmark.", this);
			Debug.LogWarning($"[Benchmark] skipped {next.id}.", this);
		}

		// Queue drained without anything starting.
		FinishBatch();
	}

	void HandleCompleted(BenchmarkRunner completed)
	{
		if (batchFolder != null) { RecordBatchEntry(completed); }

		if (queue.Count > 0) { advanceQueue = true; return; }

		FinishBatch();
	}

	/// <summary>Copies the finished run's results - the runner clears them on its next
	/// StartRun, which is the very next thing that happens in a batch.</summary>
	void RecordBatchEntry(BenchmarkRunner completed)
	{
		var entry = new BenchmarkWriter.BatchEntry
		{
			benchmarkId = completed.benchmark != null ? completed.benchmark.id : "?",
			runFolder = completed.RunFolder,
			mode = completed.mode
		};

		foreach (BenchmarkWriter.PassResult pass in completed.PassResults) { entry.passes.Add(pass); }
		batchEntries.Add(entry);
	}

	void FinishBatch()
	{
		if (batchFolder == null) { return; }

		if (batchEntries.Count > 0)
		{
			BenchmarkWriter.WriteBatchSummary(batchFolder, batchEntries, runner.machineLabel);
			Debug.Log($"[Benchmark] batch complete: {batchEntries.Count} benchmark(s) " +
				$"summarised in {batchFolder}", this);
		}

		// Restored even on abort, so a later single run does not silently write into the
		// batch folder.
		runner.outputRootOverride = savedOutputOverride;
		batchFolder = null;
		savedOutputOverride = null;
	}

	// ------------------------------------------------------------------ overlay

	void OnGUI()
	{
		if (!showOverlay || runner == null) { return; }

		// While running the overlay is suppressed: it would be captured into every
		// screenshot and its draw calls would land in the counters being recorded.
		if (runner.IsRunning && !(showProgressDuringRun && !runner.IsCaptureRun)) { return; }

		// Bare GUIStyle rather than GUI.skin.label - the editor skin's label is dark text
		// meant for a light background, and a copied style can lose an overridden colour
		// across a domain reload. Colour and size are re-applied every frame.
		if (overlayStyle == null)
		{
			overlayStyle = new GUIStyle { richText = false, wordWrap = false };
			overlayStyle.padding = new RectOffset(12, 12, 10, 10);
		}
		overlayStyle.fontSize = Mathf.Max(1, overlayFontSize);
		overlayStyle.normal.textColor = overlayTextColour;

		if (overlayBackground == null || appliedBackgroundColour != overlayBackgroundColour)
		{
			if (overlayBackground == null)
			{
				overlayBackground = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
			}
			overlayBackground.SetPixel(0, 0, overlayBackgroundColour);
			overlayBackground.Apply();
			appliedBackgroundColour = overlayBackgroundColour;
		}

		var content = new GUIContent(runner.IsRunning ? ProgressText() : IdleText());
		Vector2 size = overlayStyle.CalcSize(content);

		// Top left. It used to sit bottom-left to avoid fighting the time overlay, which this panel
		// now contains.
		var rect = new Rect(10f, 10f, size.x, size.y);

		GUI.DrawTexture(rect, overlayBackground);
		GUI.Label(rect, content, overlayStyle);

		// Top right, opposite the control panel, so neither has to be moved to read the other.
		if (showPerformance && !string.IsNullOrEmpty(perfText))
		{
			var perfContent = new GUIContent(perfText);
			Vector2 perfSize = overlayStyle.CalcSize(perfContent);
			var perfRect = new Rect(Screen.width - perfSize.x - 10f, 10f, perfSize.x, perfSize.y);

			GUI.DrawTexture(perfRect, overlayBackground);
			GUI.Label(perfRect, perfContent, overlayStyle);
		}
	}

/// <summary>
	/// The time half of the panel: what time it is and how fast it is moving.
	///
	/// Sun elevation, dayT and yearT were dropped. They are derived quantities that were useful
	/// while the solar model was being built and are noise now - and every line here is a line that
	/// has to be read past to find the one being looked for.
	/// </summary>
	string TimeText()
	{
		if (time == null || time.solarSystem == null) { return ""; }

		string speed = time.solarSystem.animate ? $"{time.CurrentSpeed:0.##}x" : "paused";

		return
			$"solar {SolarTime.FormatHours(time.CurrentLocalSolarHours)}   {speed}\n" +
			$"  1 sunrise  2 noon  3 sunset  4 midnight  5 golden\n" +
			$"  {KeyName(time.slowerKey)}{KeyName(time.fasterKey)} speed   {KeyName(time.pauseKey)} pause\n" +
			"\n";
	}

	string IdleText()
	{
		List<BenchmarkDefinition> definitions = Available;

		string name = definitions.Count == 0
			? "(none - populate availableBenchmarks)"
			: selected == AllIndex
				? $"All ({definitions.Count})"
				: definitions[Mathf.Clamp(selected, 0, definitions.Count - 1)].id;

		Clouds.CloudEffect clouds = FindClouds();

		// The profile LIST is gone, and so are the frame-count estimate, the enabled-effect list and
		// the world stats other than scale. All of them were answerable from elsewhere; what is left
		// is the state that decides what a keypress will do next.
		return
			TimeText() +
			$"profile     {PreviewName()}\n" +
			$"scale       {WorldScaleName()}\n" +
			(clouds == null ? "" : $"genus       {clouds.genusOverride}\n") +
			$"mode        {runner.mode}\n" +
			$"benchmark   {name}\n" +
			$"resolution  {ResolutionName()}\n" +
			$"  {KeyName(cycleProfileKey)} profile  {KeyName(WorldScaleKey())} scale  " +
			$"{KeyName(cycleGenusKey)} genus  {KeyName(cycleModeKey)} mode\n" +
			$"  {KeyName(runKey)} run  {KeyName(cycleBenchmarkKey)} benchmark  " +
			$"{KeyName(toggleOverlayKey)} hide  {KeyName(fullscreenKey)} fullscreen  " +
			$"{KeyName(cycleResolutionKey)} resolution  {KeyName(togglePerformanceKey)} perf\n" +
			CameraKeyLine();
	}

	/// <summary>
	/// The world scale in effect, or nothing at all when no controller is present.
	///
	/// Resolved on demand rather than cached: the controller is not required for a benchmark,
	/// and a run left as authored should show no scale line at all rather than a stale one.
	/// </summary>
	/// <summary>
	/// The keys that are not on the function row: camera mode, reset, movement, abort.
	///
	/// Named from the components that own them rather than hardcoded, so a rebind cannot leave the
	/// panel advertising a key that does nothing - which is exactly what the function row did after
	/// the remap, because the scene still held the previous bindings and the labels print those.
	/// </summary>
	string CameraKeyLine()
	{
		TestbedCamera cam = runner != null ? runner.sceneRefs.testbedCamera : null;
		if (cam == null) { return ""; }

		return $"  {KeyName(cam.toggleModeKey)} {cam.mode}  {KeyName(cam.resetKey)} reset  " +
			$"WASD/QE/RF move  {KeyName(abortKey)} abort run\n";
	}

	/// <summary>
	/// The target resolution, and the real one when they differ.
	///
	/// They differ constantly in the editor, where Screen.SetResolution is ignored and the Game
	/// view size wins - and a panel that showed only the target would then be quietly describing a
	/// frame nobody is rendering. BenchmarkWriter treats the same mismatch as significant enough to
	/// record in run.json.
	/// </summary>
	string ResolutionName()
	{
		Vector2Int target = runner.targetResolution;
		bool matches = Screen.width == target.x && Screen.height == target.y;

		return matches
			? $"{target.x}x{target.y}"
			: $"{target.x}x{target.y}  (actual {Screen.width}x{Screen.height})";
	}

	string WorldScaleName()
	{
		// ScaleLabel, not StatusLine: the long form carries the radius in km, the horizon air mass
		// against Earth's and the density multiplier, which belong in the console when justifying a
		// preset rather than on screen while flying around.
		return WorldScale != null ? WorldScale.ScaleLabel : "as authored";
	}

	/// <summary>The scale key, so the panel names the real binding rather than a hardcoded F2.</summary>
	KeyCode WorldScaleKey()
	{
		return WorldScale != null ? WorldScale.cycleKey : KeyCode.None;
	}

	WorldScaleController worldScaleCached;
	bool worldScaleSearched;

	WorldScaleController WorldScale
	{
		get
		{
			if (!worldScaleSearched)
			{
				worldScaleSearched = true;
				worldScaleCached = FindFirstObjectByType<WorldScaleController>();
			}
			return worldScaleCached;
		}
	}

	/// <summary>
	/// What is on screen right now. Reads back the live sky from RenderingManager rather than
	/// echoing the requested profile, so a profile that failed to apply cannot claim on screen
	/// that it worked - the same reasoning the run metadata uses.
	/// </summary>
	string PreviewName()
	{
		List<RendererProfile> profiles = LivePreviewProfiles();

		string requested = previewIndex >= 0 && previewIndex < profiles.Count
			? profiles[previewIndex].id
			: "scene as authored";

		// The active sky mode used to be appended here. Every arm names its atmosphere, so it was
		// restating the answer next to the question - and it pushed the value itself off to the
		// right, which is the thing being read.
		return requested;
	}

	string ProgressText()
	{
		int total = runner.Plan != null ? runner.Plan.Length : 0;
		float fraction = total > 0 ? (float)runner.FrameCursor / total : 0f;

		return
			$"RUNNING  {runner.benchmark.id}  ({runner.mode})\n" +
			$"  frame {runner.FrameCursor.ToString("N0", Ci)} / {total.ToString("N0", Ci)}  " +
			$"{(fraction * 100f).ToString("F0", Ci)}%" +
			(queue.Count > 0 ? $"   {queue.Count} queued" : "") +
			$"\n  {KeyName(abortKey)} abort";
	}

	static string KeyName(KeyCode key) => key == KeyCode.None ? "-" : key.ToString();
}
