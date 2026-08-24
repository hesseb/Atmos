using UnityEngine;

[CreateAssetMenu(menuName = "PostProcessing/Effect")]
public class PostProcessingEffect : ScriptableObject
{
	public bool enabled = true;
	public Shader shader;
	protected Material material;
	RenderTexture target;
	protected Camera cam;

	public virtual void OnEnable()
	{
		CreateMaterial(ref material, shader);
	}

	public void SetCamera(Camera cam) {
		this.cam = cam;
	}

	public virtual RenderTexture Render(RenderTexture source)
	{
		CreateMaterial(ref material, shader);

		target = RenderTexture.GetTemporary(source.descriptor);
		RenderEffectToTarget(source, target);
		return target;
	}

	public Material GetMaterial
	{
		get
		{
			return material;
		}
	}

	public virtual void OnFinishedDrawingFrame()
	{
		// Nulled after release, and it has to be. PostProcessingManager calls this on every
		// non-null effect regardless of its `enabled` flag, so without this an effect that has
		// rendered once and is then disabled hands the SAME temporary back to Unity's pool on
		// every subsequent frame, forever. The pool is then free to lend one texture to two
		// effects at once, and the second writer wins.
		//
		// Every benchmark arm switches these assets on and off - `PostProcessRendererProfile`
		// toggles the atmosphere, the cheap aerial perspective and both cloud renderers - so the
		// disabled-but-previously-rendered state is not an edge case here, it is what half the
		// profiles are made of.
		if (target)
		{
			RenderTexture.ReleaseTemporary(target);
			target = null;
		}
	}

	public virtual void OnDestroy()
	{

	}

	public virtual void DrawGizmos()
	{

	}

	protected virtual void RenderEffectToTarget(RenderTexture source, RenderTexture target)
	{
		Graphics.Blit(source, target, material);
	}

	protected void CreateMaterial(ref Material mat, Shader shader)
	{
		if (mat == null || mat.shader != shader)
		{
			if (shader == null)
			{
				Debug.LogError("Shader is null, falling back to Unlit/Texture.");
				shader = Shader.Find("Unlit/Texture");
			}
			mat = new Material(shader);
			mat.hideFlags = HideFlags.HideAndDontSave;
		}

	}

}