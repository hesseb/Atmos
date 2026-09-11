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

		// Guarded like every other reference here. Under [ExecuteInEditMode] this runs during
		// scene load and on domain reload, and there are frames in both where nothing is tagged
		// MainCamera yet - at which point the whole method has nothing to attach buffers to.
		if ( cam == null ) { return; }

		cam.RemoveAllCommandBuffers();

		outerSpaceRenderCommand = new CommandBuffer { name = "Outer Space Render" };
		starRenderer?.SetUpStarRenderingCommand( outerSpaceRenderCommand );
		moon?.Setup( outerSpaceRenderCommand );
		cam.AddCommandBuffer( CameraEvent.BeforeForwardOpaque, outerSpaceRenderCommand );

		// All sky buffers are recorded up front. Recording is free while detached, and it
		// keeps the attach/detach path down to a single Add/Remove pair - which is what lets
		// a renderer swap happen inside one frame during a benchmark pass change.
		//
		// Order matters: the outer-space buffer is added first and never removed, so every
		// sky lands after it. The sky shaders composite against the stars and moon via the
		// alpha channel, so a sky that ran first would have nothing to composite against.
		//
		// Null-guarded like the baseline below. DesiredMode already tests atmosphereEffect for
		// null, so an unassigned effect is a state the rest of this class expects to handle; only
		// this line assumed otherwise.
		if ( atmosphereEffect != null )
		{
			physicallyBasedSkyCommand = new CommandBuffer { name = "Sky Render (PBR)" };
			atmosphereEffect.SetupSkyRenderingCommand( physicallyBasedSkyCommand );
		}

		// A buffer is kept only if it actually recorded something. The alternative - attaching
		// a buffer whose material is null because a shader has not been assigned yet - blits
		// with a null material every frame, which is a far less obvious failure than simply
		// having no sky.
		if ( baselineSky != null )
		{
			baselineSkyCommand = Record( "Sky Render (Baseline)", baselineSky.RecordBaselinePass );
			nullSkyCommand = Record( "Sky Render (Null)", baselineSky.RecordNullPass );
		}

		activeMode = SkyMode.None;
		ApplyMode( DesiredMode );
	}

	static CommandBuffer Record( string name, System.Func<CommandBuffer, bool> record )
	{
		var buffer = new CommandBuffer { name = name };
		if ( record( buffer ) ) { return buffer; }

		buffer.Release();
		return null;
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
			if ( atmosphereEffect != null && atmosphereEffect.enabled ) { return SkyMode.PhysicallyBased; }
			if ( baselineSky != null && baselineSky.isActiveAndEnabled ) { return baselineSky.Mode; }
			return SkyMode.None;
		}
	}

	CommandBuffer BufferFor( SkyMode mode )
	{
		switch ( mode )
		{
			case SkyMode.PhysicallyBased: return physicallyBasedSkyCommand;
			case SkyMode.Baseline: return baselineSkyCommand;
			case SkyMode.Null: return nullSkyCommand;
			default: return null;
		}
	}

	void ApplyMode( SkyMode desired )
	{
		if ( desired == activeMode || cam == null ) { return; }

		CommandBuffer previous = BufferFor( activeMode );
		if ( previous != null ) { cam.RemoveCommandBuffer( CameraEvent.BeforeForwardOpaque, previous ); }

		CommandBuffer next = BufferFor( desired );
		if ( next != null ) { cam.AddCommandBuffer( CameraEvent.BeforeForwardOpaque, next ); }

		activeMode = desired;
		PublishSkyReflectionStrength();
	}

	/// <summary>
	/// Tells scene shaders whether there is a physically based sky worth reflecting.
	///
	/// The ocean reflects the atmosphere's sky view LUT. That LUT is only refreshed while the
	/// atmosphere effect is enabled, so in Baseline and Null mode it would simply hold whatever the
	/// last physically based frame left in it - and an ocean reflecting a frozen sky is not a
	/// defensible control condition for the comparison.
	///
	/// Gating it also draws the line cleanly for the report: with the gate, baseline and physically
	/// based images differ only in the sky pass and its effect on the ocean is excluded; the ocean's
	/// sky-derived colour is then reported as a further benefit of the physically based sky rather
	/// than being silently folded into it.
	/// </summary>
	void PublishSkyReflectionStrength()
	{
		Shader.SetGlobalFloat( "skyReflectionStrength", activeMode == SkyMode.PhysicallyBased ? 1f : 0f );
	}

	void Update()
	{
		ApplyMode( DesiredMode );

		// Republished every frame rather than only on a mode change: ApplyMode early-returns
		// when the mode is unchanged, which would leave the global unset for the first frames
		// and after a domain reload. One float is not worth an ordering bug.
		PublishSkyReflectionStrength();
	}

	/// <summary>
	/// Detaches before releasing, and forgets the buffers afterwards.
	///
	/// Releasing a command buffer that is still attached leaves the camera holding a native
	/// reference to freed memory, and it executes that buffer on every frame until something calls
	/// RemoveAllCommandBuffers - which only happens in <see cref="Setup"/>, i.e. not until this
	/// component is enabled again. Under [ExecuteInEditMode] that window opens on every domain
	/// reload, not just on teardown.
	///
	/// Only two buffers are ever attached: the outer-space one, added unconditionally in Setup and
	/// previously never removed anywhere at all, and whichever sky ApplyMode last attached. The
	/// other skies are recorded but detached, so releasing them is enough.
	///
	/// No OnDestroy is needed alongside this. Unity calls OnDisable before OnDestroy in every case
	/// where the component goes away, so a teardown that fully cleans up here covers destruction
	/// too - and a second method that ran after the fields were nulled would do nothing.
	/// </summary>
	void OnDisable()
	{
		if ( cam != null )
		{
			if ( outerSpaceRenderCommand != null )
			{
				cam.RemoveCommandBuffer( CameraEvent.BeforeForwardOpaque, outerSpaceRenderCommand );
			}

			CommandBuffer attachedSky = BufferFor( activeMode );
			if ( attachedSky != null )
			{
				cam.RemoveCommandBuffer( CameraEvent.BeforeForwardOpaque, attachedSky );
			}
		}

		outerSpaceRenderCommand?.Release();
		physicallyBasedSkyCommand?.Release();
		baselineSkyCommand?.Release();
		nullSkyCommand?.Release();

		// Nulled so a re-enable cannot attach a released buffer, and so BufferFor cannot hand one
		// back to ApplyMode. Setup reassigns all four, but only after this has run.
		outerSpaceRenderCommand = null;
		physicallyBasedSkyCommand = null;
		baselineSkyCommand = null;
		nullSkyCommand = null;

		activeMode = SkyMode.None;
	}
}
