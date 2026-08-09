using System;
using System.Collections.Generic;

/// <summary>
/// Collects undo actions and runs them in reverse on dispose.
///
/// Everything the benchmark harness changes is global or asset-level state that outlives
/// the run, so "set it and remember to put it back" is not good enough - the put-back has
/// to be registered at the moment of the change, in one place, or it eventually gets
/// missed. The worst case is not cosmetic: PostProcessingEffect.enabled is a serialized
/// field on a ScriptableObject asset, so a leaked change is written to disk.
/// </summary>
public sealed class RestoreScope : IDisposable
{
	readonly List<Action> undoActions = new List<Action>();
	bool disposed;

	public int Count => undoActions.Count;

	/// <summary>Registers an undo action to run on dispose.</summary>
	public void Add(Action undo)
	{
		if (undo != null) { undoActions.Add(undo); }
	}

	/// <summary>Applies a change and registers its undo in one step.</summary>
	public void Set<T>(Func<T> get, Action<T> set, T value)
	{
		T previous = get();
		undoActions.Add(() => set(previous));
		set(value);
	}

	public void Dispose()
	{
		if (disposed) { return; }
		disposed = true;

		// Reverse order, so changes that depend on earlier ones unwind first.
		for (int i = undoActions.Count - 1; i >= 0; i--)
		{
			try
			{
				undoActions[i]?.Invoke();
			}
			catch (Exception e)
			{
				// One failing undo must not strand the rest - especially the ones that
				// write back asset state.
				UnityEngine.Debug.LogError($"RestoreScope: an undo action threw, continuing. {e}");
			}
		}
		undoActions.Clear();
	}
}
