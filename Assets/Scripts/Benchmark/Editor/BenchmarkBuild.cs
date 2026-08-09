using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

using IOPath = System.IO.Path;

/// <summary>
/// Builds a standalone player for benchmarking, and bakes the commit it was built from.
///
/// Editor runs are never authoritative - editor overhead and an unpinned Game view size make
/// them indicative only - so every number that goes in the report has to come from a build.
/// This exists so making that build is one menu item rather than a checklist someone gets
/// wrong once and then cannot explain.
///
/// The build is a **development build**: Unity strips much of the profiler from release
/// players, and the Render counters the harness records are among the casualties. The
/// development flag costs some CPU overhead, but it applies identically to every profile, so
/// a like-for-like delta survives it - whereas a missing counter does not.
/// </summary>
static class BenchmarkBuild
{
	const string StampFolder = "Assets/Resources";
	const string StampPath = StampFolder + "/BuildStamp.asset";

	/// <summary>
	/// The default. Keeps the profiler alive, so the harness records draw calls, triangles
	/// and memory - which feed scene_hash and the self-check's geometry comparison, the thing
	/// that distinguishes a one-frame stall from a systematic difference.
	/// </summary>
	[MenuItem("Testbed/Benchmark/Build Standalone Player (Development)")]
	static void BuildDevelopment() { Build(development: true); }

	/// <summary>
	/// No profiler overhead, and the closest thing to shipping conditions.
	///
	/// Frame timings survive: FrameTimingManager is not a development-build feature and works
	/// given enableFrameTimingStats, which this project sets. The ProfilerRecorder counters
	/// are the open question - Unity strips much of the profiler from a release player, and
	/// exactly which Render counters survive in 6000.3.21f1 is not something to assume.
	///
	/// Build both and compare `counters_available` in the two run.json files: that turns the
	/// question into a measured fact, and the frame-time difference between them is the cost
	/// of the development flag.
	/// </summary>
	[MenuItem("Testbed/Benchmark/Build Standalone Player (Release)")]
	static void BuildRelease() { Build(development: false); }

	static void Build(bool development)
	{
		string kind = development ? "development" : "release";

		string directory = EditorUtility.SaveFolderPanel(
			$"Build {kind} benchmark player to", "", "");
		if (string.IsNullOrEmpty(directory)) { return; }

		BuildStamp stamp = WriteStamp();
		if (stamp.dirty)
		{
			bool proceed = EditorUtility.DisplayDialog("Uncommitted changes",
				"The working tree is dirty, so results from this build cannot be reproduced " +
				"from its commit alone. run.json will record the dirty flag and summary.md " +
				"will carry a banner.\n\nBuild anyway?", "Build", "Cancel");
			if (!proceed) { return; }
		}

		// Each kind gets its own subfolder. A player is an exe plus a _Data folder and
		// several DLLs, so building both into one directory would have the second silently
		// overwrite the first - and results live beside the exe, so they would mix too.
		string product = SanitiseProduct();
		string target = IOPath.Combine(directory, $"{product}-{kind}");
		string exe = IOPath.Combine(target, $"{product}.exe");

		var options = new BuildPlayerOptions
		{
			scenes = EnabledScenes(),
			locationPathName = exe,
			target = BuildTarget.StandaloneWindows64,
			// AllowDebugging is deliberately off even for development - attaching a managed
			// debugger changes timings, and this build exists to measure them.
			options = development ? BuildOptions.Development : BuildOptions.None
		};

		if (options.scenes.Length == 0)
		{
			Debug.LogError("[Benchmark] no enabled scenes in Build Settings.");
			return;
		}

		BuildReport report = BuildPipeline.BuildPlayer(options);
		BuildSummary summary = report.summary;

		if (summary.result != BuildResult.Succeeded)
		{
			Debug.LogError($"[Benchmark] {kind} build {summary.result}: " +
				$"{summary.totalErrors} error(s).");
			return;
		}

		Debug.Log($"[Benchmark] built {kind} player: {exe}\n" +
			$"  commit  {stamp.commit}{(stamp.dirty ? " (dirty)" : "")} on {stamp.branch}\n" +
			$"  size    {summary.totalSize / (1024 * 1024)} MB\n" +
			$"  results {IOPath.Combine(target, "BenchmarkResults")}\n" +
			(development
				? "  profiler counters expected available\n"
				: "  profiler counters likely stripped - check counters_available in run.json\n") +
			"\nRun it windowed - NOT with -batchmode, where WaitForEndOfFrame never resumes " +
			"and the harness would hang. Example:\n" +
			$"  \"{exe}\" -benchmark framing -mode selfcheck -machine \"desktop\" -quitWhenDone");

		EditorUtility.RevealInFinder(exe);
	}

	// Unity requires a menu item to return void, so the reusable form is separate.
	[MenuItem("Testbed/Benchmark/Refresh Build Stamp")]
	static void RefreshBuildStamp() { WriteStamp(); }

	/// <summary>
	/// Bakes the current commit into Assets/Resources so the player can record it. A build
	/// has no .git beside the executable, so without this every result would record commit
	/// "unknown" and be untraceable.
	///
	/// The asset is gitignored: it changes on every build, and committing it would dirty the
	/// working tree, which would then make the *next* stamp report dirty for no reason.
	/// </summary>
	static BuildStamp WriteStamp()
	{
		System.IO.Directory.CreateDirectory(StampFolder);

		GitInfo.TryGet(out string commit, out string branch, out bool dirty);

		BuildStamp stamp = AssetDatabase.LoadAssetAtPath<BuildStamp>(StampPath);
		bool created = stamp == null;
		if (created) { stamp = ScriptableObject.CreateInstance<BuildStamp>(); }

		stamp.commit = commit;
		stamp.branch = branch;
		stamp.dirty = dirty;
		stamp.builtUtc = System.DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
		stamp.unityVersion = Application.unityVersion;

		// Overwrite in place when it exists: CreateAsset would mint a new GUID on every
		// build, churning the asset and any reference to it.
		if (created) { AssetDatabase.CreateAsset(stamp, StampPath); }
		else { EditorUtility.SetDirty(stamp); }

		AssetDatabase.SaveAssets();

		Debug.Log($"[Benchmark] build stamp: {commit}{(dirty ? " (dirty)" : "")} on {branch}");
		return stamp;
	}

	static string[] EnabledScenes()
	{
		var scenes = new List<string>();
		foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
		{
			if (scene.enabled) { scenes.Add(scene.path); }
		}
		return scenes.ToArray();
	}

	static string SanitiseProduct()
	{
		string name = Application.productName;
		if (string.IsNullOrEmpty(name)) { return "Benchmark"; }

		foreach (char c in IOPath.GetInvalidFileNameChars()) { name = name.Replace(c, '-'); }
		return name.Replace(' ', '-');
	}
}
