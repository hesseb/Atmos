using UnityEngine;

/// <summary>
/// A renderer configuration to measure. Abstract so baseline and PBR renderers can both
/// slot in. Concrete subclasses must live in a file named after the class, or Unity fails
/// to bind the script to the asset.
/// </summary>
public abstract class RendererProfile : ScriptableObject
{
	// Used in filenames and in the summary table; keep it short and filename-safe.
	public string id = "profile";
	[TextArea( 1, 3 )] public string description;

	/// <summary>
	/// Applies the configuration. Register every change with the scope so it is undone
	/// when the pass ends.
	/// </summary>
	public abstract void Apply( BenchmarkSceneRefs refs, RestoreScope scope );

	/// <summary>The settings that determine this configuration's cost, recorded to run.json.</summary>
	public abstract string DescribeSettings( BenchmarkSceneRefs refs );
}
