using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Owns the camera's command buffers: the stars and moon, and exactly one sky.
///
/// Every sky renderer's buffer is created here rather than by the renderer itself, because
/// <see cref="Setup"/> calls RemoveAllCommandBuffers - a buffer added from anywhere else is
/// silently wiped the next time this component is enabled.
///
/// Which sky is attached is *derived* from the renderers' own enabled flags rather than
/// stored, so "two skies attached at once" cannot be represented. That matters because the
/// benchmark switches renderers between passes and a stuck buffer would silently double the
/// sky cost of every subsequent pass.
/// </summary>
[ExecuteInEditMode]
public class RenderingManager : MonoBehaviour
{
	public SolarSystem.StarRenderer starRenderer;
	public AtmosphereEffect atmosphereEffect;
	public BaselineSkyRenderer baselineSky;

	public Mesh mesh;
	public Material mat;
	public SolarSystem.Moon moon;

	CommandBuffer outerSpaceRenderCommand;
	CommandBuffer physicallyBasedSkyCommand;
	CommandBuffer baselineSkyCommand;
	CommandBuffer nullSkyCommand;

	SkyMode activeMode = SkyMode.None;
	Camera cam;

	/// <summary>What is actually attached right now. Recorded into run.json in preference to
	/// what a profile requested - the same principle as hashing the observed camera pose
	/// rather than the planned one.</summary>
	public SkyMode ActiveMode => activeMode;

	void OnEnable()
	{
		Setup();
	}

	void Setup()
	{
		cam = Camera.main;
		cam.RemoveAllCommandBuffers();

		outerSpaceRenderCommand = new CommandBuffer { name = "Outer Space Render" };
		starRenderer?.SetUpStarRenderingCommand(outerSpaceRenderCommand);
		moon?.Setup(outerSpaceRenderCommand);
		cam.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, outerSpaceRenderCommand);

		// All sky buffers are recorded up front. Recording is free while detached, and it
		// keeps the attach/detach path down to a single Add/Remove pair - which is what lets
		// a renderer swap happen inside one frame during a benchmark pass change.
		//
		// Order matters: the outer-space buffer is added first and never removed, so every
		// sky lands after it. The sky shaders composite against the stars and moon via the
		// alpha channel, so a sky that ran first would have nothing to composite against.
		physicallyBasedSkyCommand = new CommandBuffer { name = "Sky Render (PBR)" };
		atmosphereEffect.SetupSkyRenderingCommand(physicallyBasedSkyCommand);

		if (baselineSky != null)
		{
			baselineSkyCommand = new CommandBuffer { name = "Sky Render (Baseline)" };
			baselineSky.RecordBaselinePass(baselineSkyCommand);

			nullSkyCommand = new CommandBuffer { name = "Sky Render (Null)" };
			baselineSky.RecordNullPass(nullSkyCommand);
		}

		activeMode = SkyMode.None;
		ApplyMode(DesiredMode);
	}

	/// <summary>
	/// The physically based sky wins if its effect is enabled. That coupling is deliberate:
	/// the sky LUT compute only dispatches when AtmosphereEffect.RenderEffectToTarget has run
	/// (it is what sets lutUpdateRequired), so with the effect disabled the physically based
	/// sky buffer would blit an increasingly stale texture.
	/// </summary>
	SkyMode DesiredMode
	{
		get
		{
			if (atmosphereEffect != null && atmosphereEffect.enabled) { return SkyMode.PhysicallyBased; }
			if (baselineSky != null && baselineSky.isActiveAndEnabled) { return baselineSky.Mode; }
			return SkyMode.None;
		}
	}

	CommandBuffer BufferFor(SkyMode mode)
	{
		switch (mode)
		{
			case SkyMode.PhysicallyBased: return physicallyBasedSkyCommand;
			case SkyMode.Baseline: return baselineSkyCommand;
			case SkyMode.Null: return nullSkyCommand;
			default: return null;
		}
	}

	void ApplyMode(SkyMode desired)
	{
		if (desired == activeMode || cam == null) { return; }

		CommandBuffer previous = BufferFor(activeMode);
		if (previous != null) { cam.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, previous); }

		CommandBuffer next = BufferFor(desired);
		if (next != null) { cam.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, next); }

		activeMode = desired;
	}

	void Update()
	{
		ApplyMode(DesiredMode);
	}

	void OnDisable()
	{
		outerSpaceRenderCommand?.Release();
		physicallyBasedSkyCommand?.Release();
		baselineSkyCommand?.Release();
		nullSkyCommand?.Release();
		activeMode = SkyMode.None;
	}
}
