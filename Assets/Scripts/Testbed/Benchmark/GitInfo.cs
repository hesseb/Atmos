using UnityEngine;
using IOPath = System.IO.Path;


/// <summary>
/// Commit identity for a run. A thesis figure produced from uncommitted code is not
/// reproducible, so the dirty flag matters as much as the hash.
/// </summary>
public static class GitInfo
{
	public static bool TryGet( out string commit, out string branch, out bool dirty )
	{
		commit = "unknown";
		branch = "unknown";
		dirty = false;

#if UNITY_EDITOR
		try
		{
			string root = IOPath.GetFullPath(IOPath.Combine(Application.dataPath, ".."));
			commit = Run(root, "rev-parse --short HEAD") ?? "unknown";
			branch = Run(root, "rev-parse --abbrev-ref HEAD") ?? "unknown";
			dirty = !string.IsNullOrEmpty(Run(root, "status --porcelain"));
			return commit != "unknown";
		}
		catch
		{
			return false;
		}
#else
		// No .git beside the executable, and git may not be installed at all - so a build
		// reads the stamp baked in by BenchmarkBuild instead.
		BuildStamp stamp = BuildStamp.Load();
		if ( stamp == null ) { return false; }

		commit = stamp.commit;
		branch = stamp.branch;
		dirty = stamp.dirty;
		return commit != "unknown";
#endif
	}

#if UNITY_EDITOR
	static string Run(string workingDirectory, string arguments)
	{
		var info = new System.Diagnostics.ProcessStartInfo("git", arguments)
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using (var process = System.Diagnostics.Process.Start(info))
		{
			if (process == null) { return null; }
			string output = process.StandardOutput.ReadToEnd();
			if (!process.WaitForExit(3000)) { return null; }
			return output.Trim();
		}
	}
#endif
}
