using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

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

	[Header("Keys")]
	public KeyCode cycleBenchmarkKey = KeyCode.F2;
	public KeyCode cycleModeKey = KeyCode.F3;
	public KeyCode runKey = KeyCode.F4;
	public KeyCode abortKey = KeyCode.Escape;
	public KeyCode toggleOverlayKey = KeyCode.F6;

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
	}

	void OnEnable()
	{
		if (runner != null) { runner.onCompleted += HandleCompleted; }
	}

	void OnDisable()
	{
		if (runner != null) { runner.onCompleted -= HandleCompleted; }
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
		if (Input.GetKeyDown(runKey)) { Run(); }
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

		// Bottom left, so it does not fight the time overlay in the top left.
		var rect = new Rect(10f, Screen.height - size.y - 10f, size.x, size.y);

		GUI.DrawTexture(rect, overlayBackground);
		GUI.Label(rect, content, overlayStyle);
	}

	string IdleText()
	{
		List<BenchmarkDefinition> definitions = Available;

		string name;
		int frames, runs;

		if (definitions.Count == 0)
		{
			name = "(none - populate availableBenchmarks)";
			frames = 0;
			runs = 0;
		}
		else if (selected == AllIndex)
		{
			name = $"All ({definitions.Count})";
			frames = 0;
			foreach (BenchmarkDefinition definition in definitions)
			{
				frames += BenchmarkPlan.EstimateLength(definition);
			}
			runs = definitions.Count;
		}
		else
		{
			BenchmarkDefinition definition = definitions[Mathf.Clamp(selected, 0, definitions.Count - 1)];
			name = definition.id;
			frames = BenchmarkPlan.EstimateLength(definition);
			runs = 1;
		}

		int passes = PassesPerRun();
		long total = (long)frames * passes;

		string sizeLine = runs == 0
			? "        -"
			: $"{total.ToString("N0", Ci)} frames  ({frames.ToString("N0", Ci)} x {passes} pass" +
			  (passes == 1 ? "" : "es") + (runs > 1 ? $" x {runs} runs" : "") + ")";

		return
			"BENCHMARK\n" +
			$"  benchmark   {name}\n" +
			$"  mode        {runner.mode}{ModeNote()}\n" +
			$"  profiles    {ProfileList()}\n" +
			$"  size        {sizeLine}\n" +
			$"  {KeyName(cycleBenchmarkKey)} benchmark   {KeyName(cycleModeKey)} mode   " +
			$"{KeyName(runKey)} run   {KeyName(toggleOverlayKey)} hide" +
			(string.IsNullOrEmpty(lastMessage) ? "" : $"\n  {lastMessage}");
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

	string ModeNote()
	{
		return runner.mode switch
		{
			BenchmarkRunMode.Capture => "   (images, no statistics)",
			BenchmarkRunMode.SelfCheck => "   (repeats, writes noise floor)",
			_ => "   (statistics, no images)"
		};
	}

	string ProfileList()
	{
		if (runner.profiles == null || runner.profiles.Length == 0) { return "scene as authored"; }

		var ids = new List<string>();
		foreach (RendererProfile profile in runner.profiles)
		{
			if (profile != null) { ids.Add(profile.id); }
		}
		return ids.Count > 0 ? string.Join(", ", ids) : "scene as authored";
	}

	/// <summary>Mirrors BenchmarkRunner.BuildPassPlans so the size shown matches what runs.</summary>
	int PassesPerRun()
	{
		int profileCount = 0;
		if (runner.profiles != null)
		{
			foreach (RendererProfile profile in runner.profiles)
			{
				if (profile != null) { profileCount++; }
			}
		}
		if (profileCount == 0) { profileCount = 1; }

		int repeats = runner.IsCaptureRun
			? 1
			: Mathf.Max(runner.mode == BenchmarkRunMode.SelfCheck ? 2 : 1, runner.repeats);

		return profileCount * repeats;
	}

	static string KeyName(KeyCode key) => key == KeyCode.None ? "-" : key.ToString();
}
