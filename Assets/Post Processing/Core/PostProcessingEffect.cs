using UnityEngine;

[CreateAssetMenu( menuName = "PostProcessing/Effect" )]
public class PostProcessingEffect : ScriptableObject
{
	public bool enabled = true;

	public Shader shader;
	protected Material material;
	protected Camera cam;
	RenderTexture target;

	public virtual void OnEnable()
	{
		CreateMaterial( ref material, shader );
	}

	public void SetCamera( Camera cam )
	{
		this.cam = cam;
	}

	public virtual RenderTexture Render( RenderTexture source )
	{
		CreateMaterial( ref material, shader );

		target = RenderTexture.GetTemporary( source.descriptor );
		RenderEffectToTarget( source, target );
		return target;
	}

	public Material GetMaterial
	{
		get { return material; }
	}

	public virtual void OnFinishedDrawingFrame()
	{
		// Need to null to avoid leaving a stale target and having multiple effects write at once
		if ( target )
		{
			RenderTexture.ReleaseTemporary( target );
			target = null;
		}
	}

	public virtual void OnDestroy() { }
	public virtual void DrawGizmos() { }

	protected virtual void RenderEffectToTarget( RenderTexture source, RenderTexture target )
	{
		Graphics.Blit( source, target, material );
	}

	protected void CreateMaterial( ref Material mat, Shader shader )
	{
		if ( mat == null || mat.shader != shader )
		{
			if ( shader == null )
			{
				Debug.LogError( "Shader is null, falling back to Unlit/Texture." );
				shader = Shader.Find( "Unlit/Texture" );
			}
			mat = new Material( shader );
			mat.hideFlags = HideFlags.HideAndDontSave;
		}

	}

}
