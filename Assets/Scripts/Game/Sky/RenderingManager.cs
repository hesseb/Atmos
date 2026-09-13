using UnityEngine;
using UnityEngine.Rendering;

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

	public SkyMode ActiveMode => activeMode;

	void OnEnable()
	{
		Setup();
	}

	void Setup()
	{
		cam = Camera.main;

		if ( cam == null ) { return; }

		cam.RemoveAllCommandBuffers();

		outerSpaceRenderCommand = new CommandBuffer { name = "Outer Space Render" };
		starRenderer?.SetUpStarRenderingCommand( outerSpaceRenderCommand );
		moon?.Setup( outerSpaceRenderCommand );
		cam.AddCommandBuffer( CameraEvent.BeforeForwardOpaque, outerSpaceRenderCommand );

		if ( atmosphereEffect != null )
		{
			physicallyBasedSkyCommand = new CommandBuffer { name = "Sky Render (PBR)" };
			atmosphereEffect.SetupSkyRenderingCommand( physicallyBasedSkyCommand );
		}

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
		if ( record( buffer ) )
		{
			return buffer;
		}

		buffer.Release();
		return null;
	}

	SkyMode DesiredMode
	{
		get
		{
			if ( atmosphereEffect != null && atmosphereEffect.enabled )
			{
				return SkyMode.PhysicallyBased;
			}
			if ( baselineSky != null && baselineSky.isActiveAndEnabled )
			{
				return baselineSky.Mode;
			}
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
		if ( previous != null )
		{
			cam.RemoveCommandBuffer( CameraEvent.BeforeForwardOpaque, previous );
		}

		CommandBuffer next = BufferFor( desired );
		if ( next != null )
		{
			cam.AddCommandBuffer( CameraEvent.BeforeForwardOpaque, next );
		}

		activeMode = desired;
		PublishSkyReflectionStrength();
	}

	void PublishSkyReflectionStrength()
	{
		Shader.SetGlobalFloat( "skyReflectionStrength", activeMode == SkyMode.PhysicallyBased ? 1f : 0f );
	}

	void Update()
	{
		ApplyMode( DesiredMode );
		PublishSkyReflectionStrength();
	}

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

		outerSpaceRenderCommand = null;
		physicallyBasedSkyCommand = null;
		baselineSkyCommand = null;
		nullSkyCommand = null;

		activeMode = SkyMode.None;
	}
}
