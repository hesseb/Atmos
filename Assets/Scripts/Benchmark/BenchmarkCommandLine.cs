using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Parses benchmark options from the player's command line.
///
/// Exists so a run can be scripted: a thesis result should be reproducible by re-running a
/// command, not by remembering which fields were set in an inspector. Anything not passed
/// keeps whatever the scene authored, so the same build serves both interactive and
/// scripted use.
///
/// Recognised options:
///   -benchmark &lt;id&gt;         one of the runner's availableBenchmarks, by id
///   -profiles &lt;a,b&gt;         renderer profile ids, comma separated; omit for all
///   -repeats &lt;n&gt;
///   -mode &lt;timing|capture|selfcheck&gt;
///   -benchmarkOutput &lt;dir&gt;
///   -resolution &lt;WxH&gt;
///   -machine &lt;label&gt;        free text recorded in run.json
///   -strict                 exit non-zero if GPU timing is unavailable
///   -quitWhenDone           quit the player when the run finishes
/// </summary>
public class BenchmarkCommandLine
{
	static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

	public string benchmarkId, machineLabel, outputRoot;
	public List<string> profileIds;
	public int repeats = -1;
	public BenchmarkRunMode? mode;
	public Vector2Int? resolution;
	public bool strict, quitWhenDone;
	/// <summary>True if any benchmark option was present. Without this the runner cannot
	/// tell "launched normally" from "launched to run a benchmark".</summary>
	public bool Requested { get; private set; }
	public readonly List<string> errors = new List<string>();

	public static BenchmarkCommandLine Parse(string[] args)
	{
		var result = new BenchmarkCommandLine();
		if (args == null) { return result; }

		for (int i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "-benchmark":
					result.benchmarkId = Value(args, ref i, result, "-benchmark");
					break;
				case "-profiles":
					string list = Value(args, ref i, result, "-profiles");
					if (list != null)
					{
						result.profileIds = new List<string>(list.Split(','));
						for (int p = 0; p < result.profileIds.Count; p++)
						{
							result.profileIds[p] = result.profileIds[p].Trim();
						}
					}
					break;
				case "-repeats":
					string repeats = Value(args, ref i, result, "-repeats");
					if (repeats != null)
					{
						if (int.TryParse(repeats, NumberStyles.Integer, Ci, out int n) && n >= 1)
						{
							result.repeats = n;
						}
						else { result.errors.Add($"-repeats: '{repeats}' is not a positive integer"); }
					}
					break;
				case "-mode":
					string mode = Value(args, ref i, result, "-mode");
					if (mode != null)
					{
						switch (mode.ToLowerInvariant())
						{
							case "timing": result.mode = BenchmarkRunMode.Timing; break;
							case "capture": result.mode = BenchmarkRunMode.Capture; break;
							case "selfcheck": result.mode = BenchmarkRunMode.SelfCheck; break;
							default: result.errors.Add($"-mode: unknown mode '{mode}'"); break;
						}
					}
					break;
				case "-benchmarkOutput":
					result.outputRoot = Value(args, ref i, result, "-benchmarkOutput");
					break;
				case "-resolution":
					string res = Value(args, ref i, result, "-resolution");
					if (res != null)
					{
						string[] parts = res.Split('x', 'X');
						if (parts.Length == 2
							&& int.TryParse(parts[0], NumberStyles.Integer, Ci, out int w)
							&& int.TryParse(parts[1], NumberStyles.Integer, Ci, out int h)
							&& w > 0 && h > 0)
						{
							result.resolution = new Vector2Int(w, h);
						}
						else { result.errors.Add($"-resolution: expected WxH, got '{res}'"); }
					}
					break;
				case "-machine":
					result.machineLabel = Value(args, ref i, result, "-machine");
					break;
				case "-strict":
					result.strict = true;
					result.Requested = true;
					break;
				case "-quitWhenDone":
					result.quitWhenDone = true;
					result.Requested = true;
					break;
			}
		}

		return result;
	}

	/// <summary>Reads the value following an option, recording an error if it is missing or
	/// is itself another option.</summary>
	static string Value(string[] args, ref int i, BenchmarkCommandLine result, string option)
	{
		result.Requested = true;

		if (i + 1 >= args.Length || args[i + 1].StartsWith("-"))
		{
			result.errors.Add($"{option}: missing value");
			return null;
		}

		i++;
		return args[i];
	}

	public string Describe()
	{
		var parts = new List<string>();
		if (benchmarkId != null) { parts.Add($"benchmark={benchmarkId}"); }
		if (profileIds != null) { parts.Add($"profiles={string.Join("|", profileIds)}"); }
		if (repeats > 0) { parts.Add($"repeats={repeats}"); }
		if (mode.HasValue) { parts.Add($"mode={mode.Value}"); }
		if (resolution.HasValue) { parts.Add($"resolution={resolution.Value.x}x{resolution.Value.y}"); }
		if (outputRoot != null) { parts.Add($"output={outputRoot}"); }
		if (machineLabel != null) { parts.Add($"machine={machineLabel}"); }
		if (strict) { parts.Add("strict"); }
		if (quitWhenDone) { parts.Add("quitWhenDone"); }

		return parts.Count > 0 ? string.Join(", ", parts) : "no options";
	}
}
