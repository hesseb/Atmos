using UnityEngine.Rendering;
using UnityEngine;

/// <summary>Which sky renderer is attached to the camera. Exactly one, or none.</summary>
public enum SkyMode
{
	None,
	/// <summary>The physically based sky: LUT-driven raymarched scattering.</summary>
	PhysicallyBased,
	/// <summary>The cheap baseline: a texture lookup plus a glow term.</summary>
	Baseline,
	/// <summary>Same pass, no shading. A control, not a renderer - it isolates the cost of
	/// the pass structure itself from the cost of the shading model.</summary>
	Null
}

/// <summary>
/// The one place a sky pass is recorded.
///
/// Every sky renderer goes through this, so a frame capture of any two of them differs only
/// in the shader bound to the blit. That is what makes the measured difference between them
/// attributable to the shading model rather than to the plumbing - and it is checkable: the
/// Frame Debugger event lists must be identical but for the shader name, and `frames.csv`
/// must show exactly two extra draw calls versus no sky at all.
///
/// If this ever needs to become two different shapes for two different renderers, the
/// comparison it exists to support has been broken.
/// </summary>
public static class SkyPass
{
	static readonly int TempTarget = Shader.PropertyToID("_TempSkyRenderTexture");

	/// <summary>
	/// Reads the camera target, shades it, and writes it back.
	///
	/// The read-back is not incidental: the sky shaders composite against what the stars and
	/// moon already wrote, using the alpha channel as a brightness signal. A fixed-function
	/// blend cannot express that, so the round trip through a temporary is required rather
	/// than merely convenient.
	/// </summary>
	public static void Record(CommandBuffer cmd, Material material)
	{
		cmd.GetTemporaryRT(TempTarget, -1, -1, 0, FilterMode.Bilinear);
		cmd.Blit(BuiltinRenderTextureType.CameraTarget, TempTarget, material);
		cmd.Blit(TempTarget, BuiltinRenderTextureType.CameraTarget);
		cmd.ReleaseTemporaryRT(TempTarget);
	}
}
