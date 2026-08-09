using UnityEngine;

/// <summary>
/// Build provenance, baked at build time and loaded from Resources at run time.
///
/// A standalone player has no .git beside the executable and may be on a machine without
/// git installed, so <see cref="GitInfo"/> cannot shell out the way it does in the editor.
/// Without this, every build's run.json would record commit "unknown" - and a benchmark
/// result that cannot be traced to a commit is not reproducible, which is the whole point
/// of recording it.
///
/// Written by BenchmarkBuild before BuildPipeline runs. The asset must live at
/// Assets/Resources/BuildStamp.asset; the class name must match this file's name or Unity
/// will not bind the script to the asset.
/// </summary>
public class BuildStamp : ScriptableObject
{
	public const string ResourcePath = "BuildStamp";

	public string commit = "unknown";
	public string branch = "unknown";
	/// <summary>True if the working tree had uncommitted changes when the build was made.
	/// A dirty build is not reproducible from its commit alone and must say so.</summary>
	public bool dirty;
	public string builtUtc = "";
	public string unityVersion = "";

	static BuildStamp cached;
	static bool loadAttempted;

	/// <summary>Null in the editor unless a build has been made, and on any build produced
	/// without the build script.</summary>
	public static BuildStamp Load()
	{
		if (loadAttempted) { return cached; }

		loadAttempted = true;
		cached = Resources.Load<BuildStamp>(ResourcePath);
		return cached;
	}
}
