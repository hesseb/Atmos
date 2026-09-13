using UnityEngine.Rendering;
using UnityEngine;

public enum SkyMode
{
	None,
	PhysicallyBased,
	Baseline,
	Null
}

public static class SkyPass
{
	static readonly int TempTarget = Shader.PropertyToID( "_TempSkyRenderTexture" );

	public static void Record( CommandBuffer cmd, Material material )
	{
		cmd.GetTemporaryRT( TempTarget, -1, -1, 0, FilterMode.Bilinear );
		cmd.Blit( BuiltinRenderTextureType.CameraTarget, TempTarget, material );
		cmd.Blit( TempTarget, BuiltinRenderTextureType.CameraTarget );
		cmd.ReleaseTemporaryRT( TempTarget );
	}
}
