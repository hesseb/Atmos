using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The cheap, non-physically-based sky: the control the physically based renderer is measured
/// against.
///
/// Deliberately a MonoBehaviour rather than a <see cref="PostProcessingEffect"/>, even though
/// that would have integrated with the benchmark profile system for free. `enabled` on a
/// PostProcessingEffect is a serialized field on a ScriptableObject *asset*, so a play-mode
/// change to it survives into the project and reaches disk on the next save - the entire
/// EffectStateGuard / PinEffectStates apparatus exists only to contain that. Unity discards
/// play-mode changes to scene components, so putting the switch on a component sidesteps the
/// hazard instead of extending the machinery that works around it.
///
/// The command buffer itself is owned by <see cref="RenderingManager"/>, not by this
/// component. Its Setup() calls cam.RemoveAllCommandBuffers(), so a buffer added here would
/// be silently wiped on every domain reload.
/// </summary>
[ExecuteInEditMode]
public class BaselineSkyRenderer : MonoBehaviour
{
	public enum Variant
	{
		/// <summary>2D LUT indexed by (view elevation, sun elevation). Responds to time of
		/// day and is hand-editable.</summary>
		Gradient,
		/// <summary>A single static cubemap. Cheapest, but blind to the sun moving.</summary>
		Cubemap,
		/// <summary>No shading at all - the measurement control. See DrawSkyNull.shader.</summary>
		Null
	}

	public Variant variant = Variant.Gradient;

	[Header("Shaders")]
	public Shader baselineShader;
	public Shader nullShader;

	[Header("Sky")]
	public Texture2D skyGradient;
	public Cubemap skyCubemap;
	public float skyIntensity = 1f;

	[Header("Sun")]
	[ColorUsage(false, true)] public Color sunColour = Color.white;
	public float sunDiscSize = 1f;
	public float sunDiscBlurA = 1f;
	public float sunDiscBlurB = 1f;

	[Header("Forward-scatter glow")]
	[Tooltip("The bright halo around the sun. A (view elevation, sun elevation) lookup has no " +
		"azimuth term, so without this the sky is identical in every horizontal direction.")]
	[ColorUsage(false, true)] public Color glowColour = new Color(1f, 0.85f, 0.65f);
	public float glowPower = 8f;
	public float glowStrength = 0.4f;

	[Header("Background")]
	[Tooltip("How aggressively the sky washes out stars and the moon as it brightens.")]
	public float starFadeStrength = 4f;

	// Mirrors AtmosphereEffect's own values rather than reading them, so the two renderers are
	// independent but their output pipelines are identical. Keep them in step by hand - a
	// difference here would show up as a visual difference that is not about the sky model.
	[Header("Tone mapping (match AtmosphereEffect)")]
	public float intensity = 1f;
	public float contrast = 1.45f;
	public float whitePoint = 1.1f;
	public float ditherStrength = 0.8f;
	public Texture2D blueNoise;

	[Header("Scene")]
	[Tooltip("The planet. Leave empty to resolve from SolarSystemManager - the earth orbits, " +
		"so its centre is not the origin.")]
	public Transform planet;

	public SkyMode Mode => variant == Variant.Null ? SkyMode.Null : SkyMode.Baseline;

	Material baselineMaterial;
	Material nullMaterial;
	Light sun;
	Camera cam;

	void OnEnable()
	{
		cam = Camera.main;
		// Applied at pre-cull rather than in Update or LateUpdate because the parameters
		// depend on the final camera transform, and the benchmark runner, the solar system
		// and the camera all write during the update phases.
		Camera.onPreCull -= ApplyParameters;
		Camera.onPreCull += ApplyParameters;
	}

	void OnDisable()
	{
		Camera.onPreCull -= ApplyParameters;
	}

	void OnDestroy()
	{
		DestroyMaterial(ref baselineMaterial);
		DestroyMaterial(ref nullMaterial);
	}

	static void DestroyMaterial(ref Material material)
	{
		if (material == null) { return; }

		if (Application.isPlaying) { Destroy(material); }
		else { DestroyImmediate(material); }
		material = null;
	}

	// ------------------------------------------------------------------ recording

	/// <summary>Records the shading pass. Called by RenderingManager, which owns the buffer.</summary>
	public void RecordBaselinePass(CommandBuffer cmd)
	{
		SkyPass.Record(cmd, EnsureMaterial(ref baselineMaterial, baselineShader));
	}

	/// <summary>Records the no-op control pass.</summary>
	public void RecordNullPass(CommandBuffer cmd)
	{
		SkyPass.Record(cmd, EnsureMaterial(ref nullMaterial, nullShader));
	}

	Material EnsureMaterial(ref Material material, Shader shader)
	{
		if (shader == null)
		{
			Debug.LogError("[BaselineSky] shader not assigned.", this);
			return null;
		}

		// HideAndDontSave, and rebuilt if the shader changes: this component runs in edit mode,
		// so without it every domain reload leaks a material.
		if (material == null || material.shader != shader)
		{
			DestroyMaterial(ref material);
			material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
		}
		return material;
	}

	// ------------------------------------------------------------------ parameters

	void ApplyParameters(Camera activeCamera)
	{
		if (activeCamera != cam || variant == Variant.Null) { return; }

		Material material = EnsureMaterial(ref baselineMaterial, baselineShader);
		if (material == null) { return; }

		// Keywords rather than separate materials: the variant is switched per benchmark
		// profile, and a keyword change does not require re-recording the command buffer.
		if (variant == Variant.Cubemap)
		{
			material.EnableKeyword("SKY_CUBEMAP");
			material.DisableKeyword("SKY_GRADIENT");
		}
		else
		{
			material.EnableKeyword("SKY_GRADIENT");
			material.DisableKeyword("SKY_CUBEMAP");
		}

		if (skyGradient != null) { material.SetTexture("SkyGradient", skyGradient); }
		if (skyCubemap != null) { material.SetTexture("SkyCubemap", skyCubemap); }
		if (blueNoise != null) { material.SetTexture("BlueNoise", blueNoise); }

		Vector3 planetCentre = ObserverGeometry.PlanetCentre(ref planet);
		Vector3 dirToSun = ObserverGeometry.DirectionToSun(ref sun);

		material.SetVector("planetCentre", planetCentre);
		// Computed once per frame on the CPU rather than per pixel: it does not vary across
		// the screen, and the whole point of the baseline is that it is cheap.
		material.SetFloat("sunElevation01", ObserverGeometry.SunElevation01(
			activeCamera.transform.position, planetCentre, dirToSun));
		material.SetFloat("skyIntensity", skyIntensity);

		material.SetColor("sunColour", sunColour);
		material.SetFloat("sunDiscSize", sunDiscSize);
		material.SetFloat("sunDiscBlurA", sunDiscBlurA);
		material.SetFloat("sunDiscBlurB", sunDiscBlurB);

		material.SetColor("glowColour", glowColour);
		material.SetFloat("glowPower", glowPower);
		material.SetFloat("glowStrength", glowStrength);

		material.SetFloat("starFadeStrength", starFadeStrength);

		material.SetFloat("intensity", intensity);
		material.SetFloat("contrast", contrast);
		material.SetFloat("whitePoint", whitePoint);
		material.SetFloat("ditherStrength", ditherStrength);
	}

}
