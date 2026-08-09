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
}
