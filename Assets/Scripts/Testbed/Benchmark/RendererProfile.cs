using UnityEngine;

public abstract class RendererProfile : ScriptableObject
{
	public string id = "profile";
	[TextArea( 1, 3 )] public string description;

	public abstract void Apply( BenchmarkSceneRefs refs, RestoreScope scope );
	public abstract string DescribeSettings( BenchmarkSceneRefs refs );
}
