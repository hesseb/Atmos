using System.Text;
using UnityEngine;

/// <summary>
/// Configures a renderer by toggling post-processing effects and scene objects.
///
/// This one concrete type covers both what exists today - the atmosphere on or off, driven
/// by the single AtmosphereEffect.enabled flag that also controls the sky command buffer
/// and both per-frame compute dispatches - and the baseline milestone, where a simple sky
/// object is enabled alongside the atmosphere being disabled.
/// </summary>
[CreateAssetMenu( menuName = "Testbed/Renderer Profile", fileName = "Renderer Profile" )]
public class PostProcessRendererProfile : RendererProfile
{
	[System.Serializable]
	public struct ObjectToggle
	{
		// Scene path, e.g. "Game/World/Baseline Sky". A ScriptableObject asset cannot hold
		// a reference to a scene object, so these are resolved by name at run time.
		public string scenePath;
		public bool active;
	}

	/// <summary>
	/// What the ATMOSPHERE is doing: the sky and the aerial perspective together.
	///
	/// One value rather than a list of effect toggles, and that is the point of the refactor. Sky and
	/// aerial perspective are one feature implemented twice - the physically based path does both
	/// inside AtmosphereEffect, the cheap path pairs a gradient sky with exponential fog - so a
	/// profile that could set them independently could also express combinations that correspond to
	/// no renderer, and every profile had to remember to state all three toggles or silently inherit
	/// the previous pass's.
	///
	/// There is deliberately no LeaveAlone. A benchmark pass that inherits anything is not a
	/// controlled condition, and the omission that made it possible is the thing being removed.
	/// </summary>
	public enum AtmosphereArm
	{
		/// <summary>Nothing draws a sky and there is no aerial perspective. An ablation, not a
		/// baseline - it renders a black sky.</summary>
		Off,
		/// <summary>The sky pass with a passthrough fragment. Subtract from Off to price the pass
		/// structure itself.</summary>
		Null,
		/// <summary>The null sky plus cheap aerial perspective - structurally identical to the
		/// baselines, so subtracting it isolates the sky shading alone.</summary>
		NullWithAerial,
		/// <summary>Hand-authored gradient LUT, plus exponential fog.</summary>
		Gradient,
		/// <summary>Same shader and same cost as Gradient, but the LUT is baked off the physically
		/// based renderer. Isolates authoring from cost.</summary>
		GradientBaked,
		/// <summary>Raymarched sky with multiple scattering, plus the scattering-LUT aerial
		/// perspective.</summary>
		// Pinned rather than left to auto-increment: a cubemap arm used to sit at 5 here (explored,
		// dropped - it could not respond to the sun moving), and every profile under
		// Assets/Profiles/RenderingProfiles that asks for Pbr is serialized as atmosphereArm: 6.
		Pbr = 6,
	}

	/// <summary>
	/// What the CLOUDS are doing: which renderer, which delivery, which content.
	///
	/// Also one value, for the same reason - the volumetric and the two baseline deliveries are
	/// mutually exclusive, and expressing them as independent toggles is what let both baseline
	/// deliveries draw at once while looking very nearly correct.
	/// </summary>
	public enum CloudArm
	{
		Off,
		/// <summary>The cloud pass structure with a passthrough fragment. Subtract to price the
		/// target allocation, the two blits and the composite that any cloud renderer here pays
		/// before shading anything.</summary>
		Null,
		/// <summary>Baseline, post-process delivery, procedurally authored textures. The honest
		/// simple method.</summary>
		BaselineAuthored,
		/// <summary>Baseline, post-process delivery, textures baked off the volumetric. Same shader
		/// and same cost as BaselineAuthored, so what differs is the content - which separates "the
		/// technique is weaker" from "the art is weaker".</summary>
		BaselineBaked,
		/// <summary>Baseline shading reached by rasterising a sphere per deck instead of a
		/// full-screen quad - what a game would actually ship.</summary>
		BaselineMesh,
		VolumetricHalf,
		VolumetricTemporal,
		VolumetricFull,
	}



	[Tooltip( "Scene objects to activate or deactivate, by hierarchy path." )]
	public ObjectToggle[] objects;

	[Tooltip( "What the atmosphere does. One value rather than three toggles, so a profile cannot " +
			"express a combination no renderer corresponds to, and cannot inherit one by omission." )]
	public AtmosphereArm atmosphereArm = AtmosphereArm.Pbr;

	[Tooltip( "What the clouds do. Mutually exclusive by construction - which is what stops both " +
		"baseline deliveries drawing at once while looking very nearly correct." )]
	public CloudArm cloudArm = CloudArm.Off;

	/// <summary>
	/// One of the volumetric renderer's feature dials, forced to a value.
	///
	/// These exist so a profile can differ from clouds-full by EXACTLY ONE feature, which is the
	/// only way to price that feature. Each of the four costs real time in a different shape - a tap
	/// per raymarch step, a whole extra dispatch, a tap per march step, a second density layer - and
	/// without an arm apiece a clouds-full number is their sum with no way to decompose it. Same
	/// argument that put the null arms in.
	///
	/// Left at -1 the dial is untouched, which is what every non-ablation profile wants: forcing it
	/// to the scene's value would be indistinguishable from leaving it alone until someone retunes
	/// the asset, at which point the profiles would silently pin the old number.
	/// </summary>
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

	[Tooltip( "Volumetric feature dials, for ablation arms. -1 on each leaves the scene alone." )]
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

	/// <summary>
	/// Sets every atmosphere renderer, on every arm - not only the one being turned on.
	///
	/// That is the whole reason the arm is one value. Three of these are independent objects, so
	/// leaving any of them alone lets a previous pass's choice survive into this one, and two skies
	/// drawing at once looks almost right.
	/// </summary>
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
			Debug.LogWarning( $"[Benchmark] profile '{id}' asks for cheap aerial perspective, but " +
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

		// Variant before the enable, so switching arms never briefly attaches the wrong sky.
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

		// A component, so Unity discards this on exiting play mode even if the scope somehow does
		// not run - unlike the effect flags above, which are asset state.
		scope.Set( () => baseline.enabled, v => baseline.enabled = v, wantsBaselineSky );
	}

	/// <summary>
	/// Sets all three cloud renderers, on every arm. Same reasoning as the atmosphere: they are
	/// independent objects and only one of them should ever be drawing.
	/// </summary>
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
			// Source and variant before the enable, so switching arms never briefly draws the
			// previous arm's content.
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

	/// <summary>
	/// Forces individual volumetric features off, for the ablation arms.
	///
	/// Through RestoreScope like everything else here, so a live preview unwinds cleanly and a run
	/// cannot leave the asset changed on disk - these are ScriptableObject fields, which is the
	/// hazard EffectStateGuard exists to contain.
	/// </summary>
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
		var sb = new StringBuilder();
		sb.Append( "effects: " );

		if ( refs.postProcessing != null && refs.postProcessing.effects != null )
		{
			bool first = true;
			foreach ( PostProcessingEffect effect in refs.postProcessing.effects )
			{
				if ( effect == null ) { continue; }
				if ( !first ) { sb.Append( ", " ); }
				sb.Append( effect.name ).Append( '=' ).Append( effect.enabled ? "on" : "off" );
				first = false;
			}
		}

		// Which sky ACTUALLY rendered, read back from the renderer rather than from what this
		// profile requested - the same principle as hashing the observed camera pose rather
		// than the planned one. A profile that silently failed to apply must not be able to
		// claim in run.json that it did.
		if ( refs.renderingManager != null )
		{
			sb.Append( " | sky: " ).Append( refs.renderingManager.ActiveMode );

			if ( refs.baselineSky != null && refs.renderingManager.ActiveMode == SkyMode.Baseline )
			{
				sb.Append( '/' ).Append( refs.baselineSky.variant );
			}
		}

		// The atmosphere's cost-relevant parameters. Whether the sky raymarch runs 64 or
		// 256 steps changes the result by more than most things being compared, so the
		// numbers are meaningless without it recorded.
		AtmosphereEffect atmosphere = FindAtmosphere( refs );
		if ( atmosphere != null )
		{
			sb.Append( " | atmosphere: enabled=" ).Append( atmosphere.enabled )
			  .Append( ", skySteps=" ).Append( atmosphere.numSkyScatteringSteps )
			  .Append( ", skySize=" ).Append( atmosphere.skyRenderSize.x ).Append( 'x' )
			  .Append( atmosphere.skyRenderSize.y )
			  .Append( ", aerialStepsPerSlice=" ).Append( atmosphere.aerialStepsPerSlice )
			  .Append( ", aerialLUT=" ).Append( atmosphere.aerialPerspectiveLUTSize )
			  .Append( ", transmittanceLUT=" ).Append( atmosphere.transmittanceLUTSize.x ).Append( 'x' )
			  .Append( atmosphere.transmittanceLUTSize.y );
		}

		// The clouds' cost-relevant parameters, for the same reason. Which cost mode ran, and how
		// many steps the march was allowed, move the frame time by more than most of what is being
		// compared - and the mode in particular is the whole point of the comparison, so reading it
		// back from the effect rather than from what this profile asked for is what stops a profile
		// that failed to apply claiming otherwise in run.json.
		Clouds.CloudEffect cloudEffect = FindClouds( refs );
		if ( cloudEffect != null )
		{
			sb.Append( " | clouds: enabled=" ).Append( cloudEffect.enabled )
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
			  // Shafts are a tap per RAYMARCH STEP, not per pixel, so they move the frame time by far
			  // more than the ground shadows do and have to be attributable on their own.
			  .Append( ", shaftStrength=" ).Append( cloudEffect.shaftStrength )
			  // The deck's downward-radiance field. Its own dispatch, so it moves the frame time on
			  // its own - and it is a feature the baseline arm does not have, which is a thing the
			  // RQ1 discussion has to account for rather than something a reader should infer.
			  .Append( ", skyMap=" ).Append( cloudEffect.skyMapSize.x ).Append( 'x' )
			  .Append( cloudEffect.skyMapSize.y )
			  .Append( ", skySteps=" ).Append( cloudEffect.skySteps )
			  .Append( ", skyStrength=" ).Append( cloudEffect.skyStrength )
			  // The high band is a second density layer, so it moves both the image and the frame
			  // time. The genus override replaces the weather entirely - a run taken with one pinned
			  // is a figure, not a measurement, and run.json has to be able to say which.
			  .Append( ", highBand=" ).Append( cloudEffect.highStrength )
			  .Append( ", genus=" ).Append( cloudEffect.genusOverride );
		}

		// The baseline's arm, read back off the renderer for the same reason the sky's variant is.
		// Which delivery ran and which texture source it used are the two axes the baseline is
		// compared along, so a profile that failed to apply must not be able to claim otherwise.
		Clouds.BaselineCloudEffect baselineClouds = FindBaselineClouds( refs );
		if ( baselineClouds != null )
		{
			sb.Append( " | baselineClouds: enabled=" ).Append( baselineClouds.enabled )
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

		sb.Append( " | baselineCloudMesh: " )
		  .Append( refs.baselineCloudShell == null
			  ? "absent"
			  : ( refs.baselineCloudShell.isActiveAndEnabled ? "on" : "off" ) );

		return sb.ToString();
	}

	static AerialPerspectiveSimple FindAerial( BenchmarkSceneRefs refs )
	{
		if ( refs.postProcessing == null || refs.postProcessing.effects == null ) { return null; }

		foreach ( PostProcessingEffect effect in refs.postProcessing.effects )
		{
			if ( effect is AerialPerspectiveSimple aerial ) { return aerial; }
		}
		return null;
	}

	static Clouds.BaselineCloudEffect FindBaselineClouds( BenchmarkSceneRefs refs )
	{
		if ( refs.postProcessing == null || refs.postProcessing.effects == null ) { return null; }

		foreach ( PostProcessingEffect effect in refs.postProcessing.effects )
		{
			if ( effect is Clouds.BaselineCloudEffect baseline ) { return baseline; }
		}
		return null;
	}

	static Clouds.CloudEffect FindClouds( BenchmarkSceneRefs refs )
	{
		if ( refs.postProcessing == null || refs.postProcessing.effects == null ) { return null; }

		foreach ( PostProcessingEffect effect in refs.postProcessing.effects )
		{
			if ( effect is Clouds.CloudEffect clouds ) { return clouds; }
		}
		return null;
	}

	static AtmosphereEffect FindAtmosphere( BenchmarkSceneRefs refs )
	{
		if ( refs.postProcessing == null || refs.postProcessing.effects == null ) { return null; }

		foreach ( PostProcessingEffect effect in refs.postProcessing.effects )
		{
			if ( effect is AtmosphereEffect atmosphere ) { return atmosphere; }
		}
		return null;
	}
}
