using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Last-resort recovery for post-processing effect states.
///
/// <see cref="RestoreScope"/> handles the normal path. This covers the abnormal one: a
/// crash, a script recompile, or a domain reload part-way through a run, where Dispose
/// never runs and the modified <see cref="PostProcessingEffect.enabled"/> flags are left
/// in memory to be written to disk by the next project save.
///
/// The snapshot lives in editor SessionState, which survives domain reloads but not an
/// editor restart - so a crashed run is recovered when the editor comes back, and a
/// deliberately abandoned session starts clean.
///
/// Entirely inert in a player build; asset state there is read-only anyway.
/// </summary>
public static class EffectStateGuard
{
	public const string SessionKey = "Benchmark.EffectStateGuard";

	public static void Arm(PostProcessingEffect[] effects)
	{
#if UNITY_EDITOR
		if (effects == null) { return; }

		var entries = new List<string>();
		foreach (PostProcessingEffect effect in effects)
		{
			if (effect == null) { continue; }

			string path = UnityEditor.AssetDatabase.GetAssetPath(effect);
			if (string.IsNullOrEmpty(path)) { continue; }

			entries.Add(path + "|" + (effect.enabled ? "1" : "0"));
		}

		UnityEditor.SessionState.SetString(SessionKey, string.Join("\n", entries));
#endif
	}

	public static void Disarm()
	{
#if UNITY_EDITOR
		UnityEditor.SessionState.EraseString(SessionKey);
#endif
	}

#if UNITY_EDITOR
	/// <summary>
	/// Restores any armed snapshot. Returns how many effects were put back, or -1 if
	/// nothing was armed.
	/// </summary>
	public static int Recover()
	{
		string stored = UnityEditor.SessionState.GetString(SessionKey, null);
		if (string.IsNullOrEmpty(stored)) { return -1; }

		int restored = 0;
		foreach (string line in stored.Split('\n'))
		{
			int split = line.LastIndexOf('|');
			if (split <= 0) { continue; }

			string path = line.Substring(0, split);
			bool enabled = line.Substring(split + 1) == "1";

			var effect = UnityEditor.AssetDatabase.LoadAssetAtPath<PostProcessingEffect>(path);
			if (effect == null) { continue; }

			if (effect.enabled != enabled)
			{
				effect.enabled = enabled;
				restored++;
			}
		}

		Disarm();
		return restored;
	}
#endif
}
