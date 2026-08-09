using UnityEngine;

/// <summary>
/// A renderer configuration to measure.
///
/// Abstract so the baseline renderer (milestone 4) can slot in without redesign - today
/// the only distinction that exists is whether the physically based atmosphere is enabled.
///
/// Concrete subclasses must live in a file named after the class: Unity resolves a
/// ScriptableObject's script by file name, and a mismatch produces an asset with no script
/// attached, which then cannot be assigned to anything.
/// </summary>
public abstract class RendererProfile : ScriptableObject
{
	// Used in filenames and in the summary table; keep it short and filename-safe.
	public string id = "profile";
	[TextArea(1, 3)] public string description;

	/// <summary>
	/// Applies the configuration. Every change must be registered with the scope so it is
	/// undone when the pass ends - several of these are asset-level state that would
	/// otherwise reach disk.
	/// </summary>
	public abstract void Apply(BenchmarkSceneRefs refs, RestoreScope scope);

	/// <summary>
	/// The settings that determine this configuration's cost, for run.json. These are
	/// RQ3's independent variables; a result that does not record them is not defensible.
	/// </summary>
	public abstract string DescribeSettings(BenchmarkSceneRefs refs);
}
