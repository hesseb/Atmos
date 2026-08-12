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
	public KeyCode abortKey = KeyCode.Escape;

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
	string lastMessage = "";

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
	}

	void Update()
	{
		if (runner == null) { return; }

		if (Input.GetKeyDown(toggleOverlayKey)) { showOverlay = !showOverlay; }

		if (runner.IsRunning)
		{
			// Abort is the only input accepted mid-run: everything else would change the
			// configuration of a run already in progress.
			if (Input.GetKeyDown(abortKey))
			{
				queue.Clear();
				advanceQueue = false;
				runner.Abort();
				lastMessage = "aborted";

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
			lastMessage = "no renderer profiles on the runner";
			return;
		}

		if (!runner.sceneRefs.Resolve(out string error))
		{
			lastMessage = "cannot preview: " + error;
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
			lastMessage = "preview: scene as authored";
			return;
		}

		RendererProfile profile = profiles[previewIndex];
		previewScope = new RestoreScope();
		profile.Apply(runner.sceneRefs, previewScope);
		lastMessage = $"preview: {profile.id}";
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
			lastMessage = "no CloudEffect in the post-processing chain";
			return;
		}

		var values = (Clouds.CloudEffect.Genus[])System.Enum.GetValues(typeof(Clouds.CloudEffect.Genus));
		int next = (System.Array.IndexOf(values, clouds.genusOverride) + 1) % values.Length;
		clouds.genusOverride = values[next];
		lastMessage = $"genus: {clouds.genusOverride}";
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
	void ToggleFullscreen()
	{
		// ONE assignment. Setting the mode and then toggling Screen.fullScreen fought itself:
		// assigning the mode already implies the flag, but the flag does not update until the end
		// of the frame, so the second line read the stale value and undid the first.
		bool goingFullscreen = Screen.fullScreenMode == FullScreenMode.Windowed;
		Screen.fullScreenMode = goingFullscreen
			? FullScreenMode.FullScreenWindow
			: FullScreenMode.Windowed;

		// Says what it did even in the editor, where it does nothing - the Game view is not a
		// window Unity can take fullscreen, so this only has an effect in a player.
		lastMessage = (goingFullscreen ? "fullscreen" : "windowed") +
			(Application.isEditor ? " (player only)" : "");
	}

	void CycleBenchmark()
	{
		int count = Available.Count;
		if (count == 0) { lastMessage = "no benchmarks in availableBenchmarks"; return; }

		// One past the end is "All", so the list always offers it even with a single entry.
		selected = (selected + 1) % (count + 1);
		lastMessage = "";
	}

	void CycleMode()
	{
		runner.mode = runner.mode switch
		{
			BenchmarkRunMode.Timing => BenchmarkRunMode.Capture,
			BenchmarkRunMode.Capture => BenchmarkRunMode.SelfCheck,
			_ => BenchmarkRunMode.Timing
		};
		lastMessage = "";
	}

	void Run()
	{
		List<BenchmarkDefinition> definitions = Available;
		if (definitions.Count == 0) { lastMessage = "no benchmarks in availableBenchmarks"; return; }

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
			queue.AddRange(definitions);

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
		lastMessage = "";
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
			lastMessage = $"skipped {next.id}";
		}

		// Queue drained without anything starting.
		FinishBatch();
	}

	void HandleCompleted(BenchmarkRunner completed)
	{
		if (batchFolder != null) { RecordBatchEntry(completed); }

		if (queue.Count > 0) { advanceQueue = true; return; }

		lastMessage = "run complete";
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
			lastMessage = $"batch complete ({batchEntries.Count})";
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
			(string.IsNullOrEmpty(time.LastAction) ? "" : $"  {time.LastAction}\n") +
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
			$"  {KeyName(cycleProfileKey)} profile  {KeyName(WorldScaleKey())} scale  " +
			$"{KeyName(cycleGenusKey)} genus  {KeyName(cycleModeKey)} mode\n" +
			$"  {KeyName(runKey)} run  {KeyName(cycleBenchmarkKey)} benchmark  " +
			$"{KeyName(toggleOverlayKey)} hide  {KeyName(fullscreenKey)} fullscreen\n" +
			CameraKeyLine() +
			(string.IsNullOrEmpty(lastMessage) ? "" : $"  {lastMessage}");
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
