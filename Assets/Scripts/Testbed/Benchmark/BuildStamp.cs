using UnityEngine;

/// <summary>
/// Build provenance, baked at build time and loaded from Resources at run time. A standalone
/// player has no .git beside it, so <see cref="GitInfo"/> can't shell out like the editor does.
///
/// Written by BenchmarkBuild before BuildPipeline runs. Must live at
/// Assets/Resources/BuildStamp.asset with a class name matching this file's name.
/// </summary>
public class BuildStamp : ScriptableObject
{
	public const string ResourcePath = "BuildStamp";

	public string commit = "unknown";
	public string branch = "unknown";
	/// <summary>True if the working tree had uncommitted changes when the build was made.</summary>
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
