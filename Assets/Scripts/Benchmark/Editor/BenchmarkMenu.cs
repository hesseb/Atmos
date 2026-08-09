using UnityEditor;
using UnityEngine;

static class BenchmarkMenu
{
	/// <summary>
	/// Checks the statistics against hand-computed values, including the invariant-culture
	/// formatting that this machine's sv-SE locale would otherwise break.
	///
	/// Deliberately a menu item rather than an NUnit test: an asmdef test assembly cannot
	/// reference the predefined Assembly-CSharp, where all the harness code lives.
	/// </summary>
	[MenuItem("Testbed/Benchmark/Run Stats Self-Test")]
	static void RunStatsSelfTest()
	{
		string report = BenchmarkStats.SelfTest();
		if (report.Contains("FAIL")) { Debug.LogError(report); }
		else { Debug.Log(report); }
	}

	[MenuItem("Testbed/Benchmark/Open Results Folder")]
	static void OpenResultsFolder()
	{
		string root = BenchmarkWriter.DefaultOutputRoot();
		System.IO.Directory.CreateDirectory(root);
		EditorUtility.RevealInFinder(root);
	}

	// --------------------------------------------------------------- run mode

	[MenuItem("Testbed/Benchmark/Set Mode - Timing (numbers)")]
	static void SetModeTiming() => SetMode(BenchmarkRunMode.Timing);

	[MenuItem("Testbed/Benchmark/Set Mode - Capture (images)")]
	static void SetModeCapture() => SetMode(BenchmarkRunMode.Capture);

	/// <summary>
	/// Flips the runner's mode on the scene object. A menu item rather than only an inspector
	/// field because the two modes are meant to be run back to back over the same benchmark -
	/// numbers from one, figures from the other - and hunting for a dropdown between runs
	/// invites forgetting to switch back.
	/// </summary>
	static void SetMode(BenchmarkRunMode mode)
	{
		BenchmarkRunner runner = Object.FindFirstObjectByType<BenchmarkRunner>(FindObjectsInactive.Include);
		if (runner == null)
		{
			Debug.LogWarning("[Benchmark] no BenchmarkRunner in the open scene.");
			return;
		}

		if (runner.IsRunning)
		{
			Debug.LogWarning("[Benchmark] a run is in progress - mode change ignored.", runner);
			return;
		}

		Undo.RecordObject(runner, "Set benchmark run mode");
		runner.mode = mode;
		EditorUtility.SetDirty(runner);

		Debug.Log($"[Benchmark] mode set to {mode}." + (mode == BenchmarkRunMode.Capture
			? " This run will produce images and no statistics."
			: " This run will produce statistics and no images."), runner);
	}
}
