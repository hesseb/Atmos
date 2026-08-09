using UnityEditor;
using UnityEngine;

/// <summary>
/// Runs <see cref="EffectStateGuard.Recover"/> whenever the editor reloads scripts or
/// leaves play mode, so a run that died part-way through cannot leave modified effect
/// states to be written to disk by the next project save.
/// </summary>
[InitializeOnLoad]
static class EffectStateRecovery
{
	static EffectStateRecovery()
	{
		// Deferred: AssetDatabase is not reliably usable from a static constructor.
		EditorApplication.delayCall += RecoverIfArmed;
		EditorApplication.playModeStateChanged += OnPlayModeChanged;
	}

	static void OnPlayModeChanged(PlayModeStateChange change)
	{
		if (change == PlayModeStateChange.EnteredEditMode) { RecoverIfArmed(); }
	}

	static void RecoverIfArmed()
	{
		int restored = EffectStateGuard.Recover();
		if (restored < 0) { return; }

		if (restored > 0)
		{
			Debug.LogWarning($"[Benchmark] a run did not shut down cleanly; restored {restored} " +
				"post-processing effect state(s) to their authored values. Check that no " +
				"effect asset shows as modified in version control.");
		}
	}
}
