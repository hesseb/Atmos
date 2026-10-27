using UnityEngine;

public static class CloudShadowGlobals
{
	static Texture map;
	public static Texture Map => map != null ? map : Texture2D.whiteTexture;
	public static float ShaftStrength { get; private set; }
	public static float DeckRadius { get; private set; }

	public static void Publish( Texture shadowMap, float shadowStrength, float shaftStrength, float deckRadius )
	{
		map = shadowMap;
		ShaftStrength = shaftStrength;
		DeckRadius = deckRadius;

		Shader.SetGlobalTexture( "CloudShadowMap", shadowMap );
		Shader.SetGlobalFloat( "cloudShadowStrength", shadowStrength );
		Shader.SetGlobalFloat( "cloudShaftStrength", shaftStrength );
		Shader.SetGlobalFloat( "cloudDeckRadius", deckRadius );
	}

	public static void PublishDeckRadius( float deckRadius )
	{
		DeckRadius = deckRadius;
		Shader.SetGlobalFloat( "cloudDeckRadius", deckRadius );
	}

	public static void Retract()
	{
		ShaftStrength = 0f;
		Shader.SetGlobalFloat( "cloudShadowStrength", 0f );
		Shader.SetGlobalFloat( "cloudShaftStrength", 0f );
	}

	public static void BindTo( ComputeShader compute, int kernel )
	{
		if ( compute == null )
		{
			return;
		}

		compute.SetTexture( kernel, "CloudShadowMap", Map );
		compute.SetFloat( "cloudShaftStrength", ShaftStrength );
		compute.SetFloat( "cloudDeckRadius", DeckRadius );
	}
}
