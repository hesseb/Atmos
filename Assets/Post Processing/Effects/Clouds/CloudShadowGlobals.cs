using UnityEngine;

/// <summary>
/// The cloud deck's shadow, published once and readable two ways.
///
/// Shader globals are enough for fragment shaders - Terrain, Ocean and the cloud renderers all pick
/// up CloudShadowMap through Shader.SetGlobalTexture without anyone binding anything. Compute
/// shaders do not work that way: every texture a kernel references has to be bound explicitly with
/// ComputeShader.SetTexture on that kernel, or the dispatch fails with
///
///     Compute shader (SkyRender): Property (CloudShadowMap) at kernel index (0) is not set
///
/// which is exactly what happened the moment the light shafts made AtmosphereCommon.hlsl reference
/// the map, since three of its consumers are computes.
///
/// So the atmosphere needs a C# handle on something the cloud renderers own. It could hold a
/// reference to CloudEffect, but that would couple the two renderers and break in the baseline arm,
/// where a different cloud renderer publishes the same globals. This holder is the shader-global
/// pattern mirrored on the C# side: whoever is drawing clouds publishes, and whoever needs the value
/// reads, with neither knowing about the other.
///
/// Publishing goes through here rather than through Shader.SetGlobal directly so there is ONE
/// writer. Two representations of the same state with two setters is how they drift.
/// </summary>
public static class CloudShadowGlobals
{
	static Texture map;

	/// <summary>
	/// The shadow map, or white when no cloud renderer has published one.
	///
	/// White rather than null, because a compute must have something bound whether or not the sample
	/// executes - and white is R = 1 is transmittance 1 is "no cloud overhead", which is the correct
	/// answer rather than merely a safe one.
	/// </summary>
	public static Texture Map => map != null ? map : Texture2D.whiteTexture;

	public static float ShaftStrength { get; private set; }
	public static float DeckRadius { get; private set; }

	public static void Publish(Texture shadowMap, float shadowStrength, float shaftStrength, float deckRadius)
	{
		map = shadowMap;
		ShaftStrength = shaftStrength;
		DeckRadius = deckRadius;

		Shader.SetGlobalTexture("CloudShadowMap", shadowMap);
		Shader.SetGlobalFloat("cloudShadowStrength", shadowStrength);
		Shader.SetGlobalFloat("cloudShaftStrength", shaftStrength);
		Shader.SetGlobalFloat("cloudDeckRadius", deckRadius);
	}

	/// <summary>
	/// The deck radius alone, for a renderer that publishes its radiance map without its shadow map.
	///
	/// Those two passes are independently switchable - shadow strength and sky strength are separate
	/// dials - so the radius has to be reachable from either. It exists here rather than as a second
	/// Shader.SetGlobalFloat elsewhere so that this file stays the only writer, which is the whole
	/// point of the holder.
	/// </summary>
	public static void PublishDeckRadius(float deckRadius)
	{
		DeckRadius = deckRadius;
		Shader.SetGlobalFloat("cloudDeckRadius", deckRadius);
	}

	/// <summary>
	/// No cloud overhead. The map itself is deliberately left bound - the strengths are what the
	/// shaders test, and dropping the texture would only cost the computes their binding again.
	/// </summary>
	public static void Retract()
	{
		ShaftStrength = 0f;
		Shader.SetGlobalFloat("cloudShadowStrength", 0f);
		Shader.SetGlobalFloat("cloudShaftStrength", 0f);
	}

	/// <summary>
	/// Binds everything a compute needs to evaluate cloudShadow or cloudSunVisibility.
	///
	/// One call rather than three, so a consumer cannot bind the texture and forget the radius - the
	/// failure mode there is silent: shafts would sample a deck at radius 0 and return 1 everywhere.
	/// </summary>
	public static void BindTo(ComputeShader compute, int kernel)
	{
		if (compute == null) { return; }

		compute.SetTexture(kernel, "CloudShadowMap", Map);
		compute.SetFloat("cloudShaftStrength", ShaftStrength);
		compute.SetFloat("cloudDeckRadius", DeckRadius);
		// Not used by any compute today - the shafts read cloudShaftStrength - but bound so a kernel
		// that starts calling cloudShadow() does not read whatever the last dispatch left.
		compute.SetFloat("cloudShadowStrength", Shader.GetGlobalFloat("cloudShadowStrength"));
	}
}
