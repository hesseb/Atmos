using System.Text;
using UnityEngine;

[CreateAssetMenu( menuName = "Testbed/Renderer Profile", fileName = "Renderer Profile" )]
public class PostProcessRendererProfile : RendererProfile
{
	[System.Serializable]
	public struct ObjectToggle
	{
		public string scenePath;
		public bool active;
	}

	public enum AtmosphereArm
	{
		Off, // Not a baseline, skips the process completely
		Null, // Baseline, goes through the process but does nothing. Compare to Off to determine cost of plumbing
		NullWithAerial,
		Gradient,
		GradientBaked,
		Pbr = 6, // Pinned for compatibility reasons
	}

	public enum CloudArm
	{
		Off, // Not a baseline, skips the process completely
		Null, // Baseline without clouds
		BaselineAuthored,
		BaselineBaked,
		BaselineMesh,
		VolumetricHalf,
		VolumetricTemporal,
		VolumetricFull,
	}

	[Tooltip( "Scene objects to activate or deactivate, by hierarchy path." )]
	public ObjectToggle[] objects;

	[Tooltip( "What the atmosphere does." )]
	public AtmosphereArm atmosphereArm = AtmosphereArm.Pbr;

	[Tooltip( "What the clouds do." )]
	public CloudArm cloudArm = CloudArm.Off;

	[System.Serializable]
	public struct FeatureDials
	{
		[Tooltip( "-1 leaves the scene's value alone. 0 disables the feature exactly." )]
		public float shaftStrength;
		public float skyStrength;
		public float aerialStrength;
		public float highStrength;

		public static FeatureDials Untouched =>
			new FeatureDials { shaftStrength = -1, skyStrength = -1, aerialStrength = -1, highStrength = -1 };
	}

	[Tooltip( "Feature dials. -1 on each leaves the scene alone." )]
	public FeatureDials dials = FeatureDials.Untouched;

	public override void Apply( BenchmarkSceneRefs refs, RestoreScope scope )
	{
		if ( objects != null )
		{
			foreach ( ObjectToggle toggle in objects )
			{
				if ( string.IsNullOrEmpty( toggle.scenePath ) ) { continue; }

				GameObject go = GameObject.Find( toggle.scenePath );
				if ( go == null )
				{
					Debug.LogWarning( $"[Benchmark] profile '{id}': no scene object at " +
						$"'{toggle.scenePath}' - skipping. Note GameObject.Find cannot see " +
						"inactive objects, so an object that starts disabled must be " +
						"activated by hand or referenced another way.", this );
					continue;
				}

				GameObject captured = go;
				scope.Set( () => captured.activeSelf, v => captured.SetActive( v ), toggle.active );
			}
		}

		ApplyAtmosphereArm( refs, scope );
		ApplyCloudArm( refs, scope );
		ApplyFeatureDials( refs, scope );
	}

	void ApplyAtmosphereArm( BenchmarkSceneRefs refs, RestoreScope scope )
	{
		bool wantsPbr = atmosphereArm == AtmosphereArm.Pbr;
		bool wantsBaselineSky = atmosphereArm != AtmosphereArm.Off && !wantsPbr;
		bool wantsAerial = atmosphereArm == AtmosphereArm.NullWithAerial ||
						   atmosphereArm == AtmosphereArm.Gradient ||
						   atmosphereArm == AtmosphereArm.GradientBaked;

		AtmosphereEffect atmosphere = FindAtmosphere( refs );
		if ( atmosphere != null )
		{
			scope.Set( () => atmosphere.enabled, v => atmosphere.enabled = v, wantsPbr );
		}
		else if ( wantsPbr )
		{
			Debug.LogWarning( $"[Benchmark] profile '{id}' asks for the physically based atmosphere, " +
				"but there is no AtmosphereEffect in the post-processing chain.", this );
		}

		AerialPerspectiveSimple aerial = FindAerial( refs );
		if ( aerial != null )
		{
			scope.Set( () => aerial.enabled, v => aerial.enabled = v, wantsAerial );
		}
		else if ( wantsAerial )
		{
			Debug.LogWarning( $"[Benchmark] profile '{id}' asks for aerial perspective, but " +
				"there is no AerialPerspectiveSimple in the chain - this arm is then one feature " +
				"short of the physically based path.", this );
		}

		BaselineSkyRenderer baseline = refs.baselineSky;
		if ( baseline == null )
		{
			if ( wantsBaselineSky )
			{
				Debug.LogWarning( $"[Benchmark] profile '{id}' asks for sky '{atmosphereArm}', but " +
					"there is no BaselineSkyRenderer in the scene.", this );
			}
			return;
		}

		if ( wantsBaselineSky )
		{
			BaselineSkyRenderer.Variant variant;
			switch ( atmosphereArm )
			{
				case AtmosphereArm.GradientBaked: variant = BaselineSkyRenderer.Variant.GradientBaked; break;
				case AtmosphereArm.Null:
				case AtmosphereArm.NullWithAerial: variant = BaselineSkyRenderer.Variant.Null; break;
				default: variant = BaselineSkyRenderer.Variant.Gradient; break;
			}
			scope.Set( () => baseline.variant, v => baseline.variant = v, variant );
		}

		scope.Set( () => baseline.enabled, v => baseline.enabled = v, wantsBaselineSky );
	}

	void ApplyCloudArm( BenchmarkSceneRefs refs, RestoreScope scope )
	{
		bool wantsVolumetric = cloudArm == CloudArm.VolumetricHalf ||
							   cloudArm == CloudArm.VolumetricTemporal ||
							   cloudArm == CloudArm.VolumetricFull;
		bool wantsMesh = cloudArm == CloudArm.BaselineMesh;
		bool wantsBaselinePost = cloudArm == CloudArm.BaselineAuthored ||
								 cloudArm == CloudArm.BaselineBaked ||
								 cloudArm == CloudArm.Null;

		Clouds.CloudEffect volumetric = FindClouds( refs );
		if ( volumetric != null )
		{
			if ( wantsVolumetric )
			{
				Clouds.CloudEffect.CostMode mode =
					cloudArm == CloudArm.VolumetricFull ? Clouds.CloudEffect.CostMode.Full :
					cloudArm == CloudArm.VolumetricTemporal ? Clouds.CloudEffect.CostMode.Temporal :
					Clouds.CloudEffect.CostMode.Half;
				scope.Set( () => volumetric.costMode, v => volumetric.costMode = v, mode );
			}
			scope.Set( () => volumetric.enabled, v => volumetric.enabled = v, wantsVolumetric );
		}
		else if ( wantsVolumetric )
		{
			Debug.LogWarning( $"[Benchmark] profile '{id}' asks for the volumetric clouds, but there " +
				"is no CloudEffect in the post-processing chain.", this );
		}

		Clouds.BaselineCloudEffect baseline = FindBaselineClouds( refs );
		if ( baseline != null )
		{
			if ( wantsBaselinePost )
			{
				var source = cloudArm == CloudArm.BaselineBaked
					? Clouds.BaselineCloudEffect.TextureSource.Baked
					: Clouds.BaselineCloudEffect.TextureSource.Authored;
				scope.Set( () => baseline.textureSource, v => baseline.textureSource = v, source );

				var variant = cloudArm == CloudArm.Null
					? Clouds.BaselineCloudEffect.Variant.Null
					: Clouds.BaselineCloudEffect.Variant.Shaded;
				scope.Set( () => baseline.variant, v => baseline.variant = v, variant );
			}
			scope.Set( () => baseline.enabled, v => baseline.enabled = v, wantsBaselinePost );
		}
		else if ( wantsBaselinePost )
		{
			Debug.LogWarning( $"[Benchmark] profile '{id}' asks for baseline clouds, but there is no " +
				"BaselineCloudEffect in the post-processing chain.", this );
		}

		Clouds.BaselineCloudShell shell = refs.baselineCloudShell;
		if ( shell != null )
		{
			scope.Set( () => shell.enabled, v => shell.enabled = v, wantsMesh );
		}
		else if ( wantsMesh )
		{
			Debug.LogWarning( $"[Benchmark] profile '{id}' asks for the drawn-mesh cloud delivery, " +
				"but there is no BaselineCloudShell in the scene - nothing will draw.", this );
		}
	}

	void ApplyFeatureDials( BenchmarkSceneRefs refs, RestoreScope scope )
	{
		Clouds.CloudEffect effect = FindClouds( refs );
		if ( effect == null ) { return; }

		if ( dials.shaftStrength >= 0 )
		{
			float v = dials.shaftStrength;
			scope.Set( () => effect.shaftStrength, x => effect.shaftStrength = x, v );
		}
		if ( dials.skyStrength >= 0 )
		{
			float v = dials.skyStrength;
			scope.Set( () => effect.skyStrength, x => effect.skyStrength = x, v );
		}
		if ( dials.aerialStrength >= 0 )
		{
			float v = dials.aerialStrength;
			scope.Set( () => effect.aerialStrength, x => effect.aerialStrength = x, v );
		}
		if ( dials.highStrength >= 0 )
		{
			float v = dials.highStrength;
			scope.Set( () => effect.highStrength, x => effect.highStrength = x, v );
		}
	}

	public override string DescribeSettings( BenchmarkSceneRefs refs )
	{
		var stringBuilder = new StringBuilder();
		stringBuilder.Append( "effects: " );

		if ( refs.postProcessing != null && refs.postProcessing.effects != null )
		{
			bool first = true;
			foreach ( PostProcessingEffect effect in refs.postProcessing.effects )
			{
				if ( effect == null )
				{
					continue;
				}

				if ( !first )
				{
					stringBuilder.Append( ", " );
				}

				stringBuilder.Append( effect.name ).Append( '=' ).Append( effect.enabled ? "on" : "off" );
				first = false;
			}
		}

		if ( refs.renderingManager != null )
		{
			stringBuilder.Append( " | sky: " ).Append( refs.renderingManager.ActiveMode );

			if ( refs.baselineSky != null && refs.renderingManager.ActiveMode == SkyMode.Baseline )
			{
				stringBuilder.Append( '/' ).Append( refs.baselineSky.variant );
			}
		}

		AtmosphereEffect atmosphere = FindAtmosphere( refs );
		if ( atmosphere != null )
		{
			stringBuilder.Append( " | atmosphere: enabled=" ).Append( atmosphere.enabled )
			  .Append( ", skySteps=" ).Append( atmosphere.numSkyScatteringSteps )
			  .Append( ", skySize=" ).Append( atmosphere.skyRenderSize.x ).Append( 'x' )
			  .Append( atmosphere.skyRenderSize.y )
			  .Append( ", aerialStepsPerSlice=" ).Append( atmosphere.aerialStepsPerSlice )
			  .Append( ", aerialLUT=" ).Append( atmosphere.aerialPerspectiveLUTSize )
			  .Append( ", transmittanceLUT=" ).Append( atmosphere.transmittanceLUTSize.x ).Append( 'x' )
			  .Append( atmosphere.transmittanceLUTSize.y );
		}

		Clouds.CloudEffect cloudEffect = FindClouds( refs );
		if ( cloudEffect != null )
		{
			stringBuilder.Append( " | clouds: enabled=" ).Append( cloudEffect.enabled )
			  .Append( ", mode=" ).Append( cloudEffect.costMode )
			  .Append( ", stepSize=" ).Append( cloudEffect.stepSize )
			  .Append( ", maxSteps=" ).Append( cloudEffect.maxSteps )
			  .Append( ", lightMarchLength=" ).Append( cloudEffect.lightMarchLength )
			  .Append( ", aerialStrength=" ).Append( cloudEffect.aerialStrength )
			  .Append( ", shell=" ).Append( cloudEffect.cloudBottomAltitude ).Append( '-' )
			  .Append( cloudEffect.cloudTopAltitude )
			  .Append( ", weatherMap=" ).Append( cloudEffect.weatherMapSize.x ).Append( 'x' )
			  .Append( cloudEffect.weatherMapSize.y )
			  .Append( ", shadowMap=" ).Append( cloudEffect.shadowMapSize.x ).Append( 'x' )
			  .Append( cloudEffect.shadowMapSize.y )
			  .Append( ", shadowStrength=" ).Append( cloudEffect.shadowStrength )
			  .Append( ", shaftStrength=" ).Append( cloudEffect.shaftStrength )
			  .Append( ", skyMap=" ).Append( cloudEffect.skyMapSize.x ).Append( 'x' )
			  .Append( cloudEffect.skyMapSize.y )
			  .Append( ", skySteps=" ).Append( cloudEffect.skySteps )
			  .Append( ", skyStrength=" ).Append( cloudEffect.skyStrength )
			  .Append( ", highBand=" ).Append( cloudEffect.highStrength )
			  .Append( ", genus=" ).Append( cloudEffect.genusOverride );
		}

		Clouds.BaselineCloudEffect baselineClouds = FindBaselineClouds( refs );
		if ( baselineClouds != null )
		{
			stringBuilder.Append( " | baselineClouds: enabled=" ).Append( baselineClouds.enabled )
			  .Append( ", variant=" ).Append( baselineClouds.variant )
			  .Append( ", source=" ).Append( baselineClouds.textureSource )
			  .Append( ", mode=" ).Append( baselineClouds.costMode )
			  .Append( ", decks=" ).Append( baselineClouds.UpperActive ? 2 : 1 )
			  .Append( ", altitudes=" ).Append( baselineClouds.lower.altitude ).Append( '/' )
			  .Append( baselineClouds.upper.altitude )
			  .Append( ", shadowMap=" ).Append( baselineClouds.shadowMapSize.x ).Append( 'x' )
			  .Append( baselineClouds.shadowMapSize.y )
			  .Append( ", shadowStrength=" ).Append( baselineClouds.shadowStrength );
		}

		stringBuilder.Append( " | baselineCloudMesh: " )
		  .Append( refs.baselineCloudShell == null
			  ? "absent"
			  : ( refs.baselineCloudShell.isActiveAndEnabled ? "on" : "off" ) );

		return stringBuilder.ToString();
	}

	static AerialPerspectiveSimple FindAerial( BenchmarkSceneRefs refs )
	{
		if ( refs.postProcessing == null || refs.postProcessing.effects == null )
		{
			return null;
		}

		foreach ( PostProcessingEffect effect in refs.postProcessing.effects )
		{
			if ( effect is AerialPerspectiveSimple aerial )
			{
				return aerial;
			}
		}

		return null;
	}

	static Clouds.BaselineCloudEffect FindBaselineClouds( BenchmarkSceneRefs refs )
	{
		if ( refs.postProcessing == null || refs.postProcessing.effects == null )
		{
			return null;
		}

		foreach ( PostProcessingEffect effect in refs.postProcessing.effects )
		{
			if ( effect is Clouds.BaselineCloudEffect baseline )
			{
				return baseline;
			}
		}

		return null;
	}

	static Clouds.CloudEffect FindClouds( BenchmarkSceneRefs refs )
	{
		if ( refs.postProcessing == null || refs.postProcessing.effects == null )
		{
			return null;
		}

		foreach ( PostProcessingEffect effect in refs.postProcessing.effects )
		{
			if ( effect is Clouds.CloudEffect clouds )
			{
				return clouds;
			}
		}

		return null;
	}

	static AtmosphereEffect FindAtmosphere( BenchmarkSceneRefs refs )
	{
		if ( refs.postProcessing == null || refs.postProcessing.effects == null )
		{
			return null;
		}

		foreach ( PostProcessingEffect effect in refs.postProcessing.effects )
		{
			if ( effect is AtmosphereEffect atmosphere )
			{
				return atmosphere;
			}
		}

		return null;
	}
}
