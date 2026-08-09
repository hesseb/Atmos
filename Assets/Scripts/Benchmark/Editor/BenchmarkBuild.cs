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

	[MenuItem("Testbed/Benchmark/Build Standalone Player")]
	static void Build()
	{
		string directory = EditorUtility.SaveFolderPanel("Build benchmark player to", "", "");
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

		string exe = IOPath.Combine(directory, $"{SanitiseProduct()}.exe");

		var options = new BuildPlayerOptions
		{
			scenes = EnabledScenes(),
			locationPathName = exe,
			target = BuildTarget.StandaloneWindows64,
			// Development: the release player strips the profiler counters the harness reads.
			// AllowDebugging is deliberately off - a managed debugger changes timings.
			options = BuildOptions.Development
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
			Debug.LogError($"[Benchmark] build {summary.result}: {summary.totalErrors} error(s).");
			return;
		}

		Debug.Log($"[Benchmark] built {exe}\n" +
			$"  commit {stamp.commit}{(stamp.dirty ? " (dirty)" : "")} on {stamp.branch}\n" +
			$"  size   {summary.totalSize / (1024 * 1024)} MB\n\n" +
			"Run it windowed - NOT with -batchmode, where WaitForEndOfFrame never resumes and " +
			"the harness would hang. Example:\n" +
			$"  \"{exe}\" -benchmark framing -mode selfcheck -resolution 1920x1080 " +
			"-machine \"desktop\" -strict -quitWhenDone");

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
