using UnityEngine;

/// <summary>
/// The cloud deck's shadow, published once and readable two ways: fragment shaders pick it up via
/// Shader.SetGlobalTexture, but compute shaders need every texture bound explicitly per kernel via
/// ComputeShader.SetTexture, or the dispatch fails. This holder mirrors the shader-global pattern in
/// C# so whoever draws clouds (PBR or baseline) can publish without coupling to the consumer, and
/// there is exactly one writer.
/// </summary>
public static class CloudShadowGlobals
{
	static Texture map;

	/// <summary>
	/// The shadow map, or white when no cloud renderer has published one - white being transmittance
	/// 1, "no cloud overhead", the correct answer rather than merely a safe one.
	/// </summary>
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

	/// <summary>
	/// The deck radius alone, for a renderer that publishes its radiance map without its shadow map -
	/// shadow strength and sky strength are independently switchable dials.
	/// </summary>
	public static void PublishDeckRadius( float deckRadius )
	{
		DeckRadius = deckRadius;
		Shader.SetGlobalFloat( "cloudDeckRadius", deckRadius );
	}

	/// <summary>
	/// No cloud overhead. The map itself stays bound - only the strengths are what shaders test.
	/// </summary>
	public static void Retract()
	{
		ShaftStrength = 0f;
		Shader.SetGlobalFloat( "cloudShadowStrength", 0f );
		Shader.SetGlobalFloat( "cloudShaftStrength", 0f );
	}

	/// <summary>
	/// Binds everything a compute needs to evaluate cloudShadow or cloudSunVisibility, in one call so
	/// a consumer can't bind the texture and forget the radius.
	/// </summary>
	public static void BindTo( ComputeShader compute, int kernel )
	{
		if ( compute == null ) { return; }

		compute.SetTexture( kernel, "CloudShadowMap", Map );
		compute.SetFloat( "cloudShaftStrength", ShaftStrength );
		compute.SetFloat( "cloudDeckRadius", DeckRadius );
	}
}
