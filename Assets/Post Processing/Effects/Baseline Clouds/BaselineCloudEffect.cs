using UnityEngine;

namespace Clouds
{
	/// <summary>
	/// The baseline cloud renderer: a textured sheet on a sphere around the globe.
	///
	/// This is the simple method RQ1 and RQ2 are phrased against - the Victoria 3 reading of a cloud
	/// texture floating over the map, adapted from a flat map to a globe. Without it the volumetric
	/// renderer has nothing to be compared to, which NOTES.md records as the blocking gap.
	///
	/// Structurally a copy of CloudEffect: the same post-process hookup, the same Full/Half cost
	/// modes, the same offscreen-then-composite shape, and the same composite shader code. Only the
	/// shading model differs. AerialPerspectiveSimple and DrawSkyBaseline are built the same way and
	/// for the same reason - hold everything but the model constant, or the measurement is of two
	/// implementations rather than of two techniques.
	///
	/// Two texture sources, switchable at run time:
	///
	///   Authored  a procedural field, what a studio would actually make. The honest baseline.
	///   Baked     the volumetric renderer flattened into the same format. Not shippable - it needs
	///             the volumetric to exist - but a control condition: the CONTENT is then identical,
	///             so any remaining visual difference is the technique rather than the art. The
	///             baseline sky's GradientBaked variant exists to provide exactly this separation.
	/// </summary>
	[CreateAssetMenu(menuName = "PostProcessing/Baseline Clouds")]
	public class BaselineCloudEffect : PostProcessingEffect
	{
		public enum TextureSource
		{
			Authored,
			Baked,
		}

		/// <summary>
		/// The same two resolution modes the volumetric has, minus Temporal.
		///
		/// Temporal is omitted rather than forgotten: it exists there to amortise a march across
		/// four frames, and there is no march here to amortise. Offering it would measure the
		/// reprojection machinery on its own, which is not a cloud technique.
		/// </summary>
		public enum CostMode
		{
			Full,
			Half,
		}

		public enum DebugMode
		{
			Off = 0,
			Opacity = 1,
			Normal = 2,
			MipLevel = 3,
		}

		/// <summary>
		/// One cloud deck. Two of them are stacked at different radii so they parallax against each
		/// other, which is the depth cue a single flat overlay cannot give on a globe.
		///
		/// Each carries both texture sources, so switching Authored to Baked swaps both decks at
		/// once and the comparison never ends up half in one condition and half in the other.
		/// </summary>
		[System.Serializable]
		public class LayerSettings
		{
			public Texture2D authored;
			public Texture2D baked;

			[Tooltip("Height above sea level, world units. Keep both inside the volumetric's shell " +
				"so the two renderers put their cloud in the same band - the A/B comparison needs " +
				"that even more than it needs either to be correct on its own.")]
			public float altitude;

			[Tooltip("How far the height channel lifts the sheet. This is what produces parallax " +
				"WITHIN a deck - the sheet is not flat, so its own features slide against one " +
				"another as the camera moves. 0 makes it a decal.")]
			[Range(0f, 8f)] public float thickness;

			[Range(0f, 4f)] public float opacity;

			[Tooltip("Broken versus overcast. A power on the stored coverage, so higher clears the " +
				"thin edges and leaves the solid cores.")]
			[Range(0.2f, 4f)] public float contrast;

			[Tooltip("How pronounced the relief shading is. The stored normal is already unit " +
				"length, so this re-steepens its tangent components and renormalises rather than " +
				"scaling it, which would only change its length.")]
			[Range(0f, 4f)] public float reliefStrength;

			[Tooltip("Multiplies the wind speed for this deck. The two must differ, or they drift " +
				"rigidly together and never separate - and the separation IS the parallax. Real " +
				"decks at different altitudes sit in different winds, so this is wind shear rather " +
				"than a cheat.")]
			public float windScale;

			public static LayerSettings Create(float altitude, float thickness, float opacity,
				float contrast, float relief, float windScale)
			{
				return new LayerSettings
				{
					altitude = altitude,
					thickness = thickness,
					opacity = opacity,
					contrast = contrast,
					reliefStrength = relief,
					windScale = windScale
				};
			}
		}

		[Header("Texture source")]
		[Tooltip("Authored is the honest baseline. Baked is the volumetric flattened into the same " +
			"format - a control condition where the content is identical, so what is left is the " +
			"technique.")]
		public TextureSource textureSource = TextureSource.Authored;

		[Header("Shell")]
		[Tooltip("Sea level, in world units. Its own field rather than the atmosphere's planetRadius " +
			"global, which is zero whenever the atmosphere is off - and the baseline has to keep " +
			"working in exactly the profiles where it is.")]
		public float bodyRadius = 150f;

		[Tooltip("The lower, denser deck.")]
		public LayerSettings lower = LayerSettings.Create(4f, 2.5f, 1.3f, 1.1f, 1f, 1f);

		[Tooltip("The upper, thinner deck. Higher, sparser and flatter so it reads as high cloud " +
			"rather than as a second copy of the same weather.")]
		public LayerSettings upper = LayerSettings.Create(8f, 1.2f, 0.75f, 1.4f, 0.7f, 1.7f);

		[Tooltip("Off drops to a single deck. A uniform branch in the shader, so the second deck " +
			"then costs exactly nothing - which is what makes one layer against two a clean " +
			"measurement of what the depth cue is worth.")]
		public bool upperLayerEnabled = true;

		[Header("Lighting")]
		[Tooltip("Sun colour across sun elevation: t=0 is 90 degrees below the horizon, t=0.5 " +
			"exactly on it, t=1 at the zenith. Author these against the sky gradient's horizon " +
			"colours - a Gradient is the most artist-facing surface Unity has, which is itself a " +
			"data point for the authoring comparison.")]
		public Gradient sunColour;

		[Tooltip("Skylight on the clouds, on the same elevation axis. This is what carries the " +
			"sunset onto the undersides, and what stops them going black at night.")]
		public Gradient ambientColour;

		[Range(0f, 4f)] public float sunIntensity = 1.4f;
		[Range(0f, 4f)] public float ambientIntensity = 1f;

		[Tooltip("Wraps the terminator around the back of the relief. Real cloud scatters light well " +
			"past the geometric terminator, and a bare N.L on a sheet reads as embossed metal.")]
		[Range(0f, 1f)] public float wrap = 0.45f;

		[Tooltip("How much sunlight reaches the underside. A sheet shaded only by its outward normal " +
			"looks identical from below, which is the one view a low camera always has.")]
		[Range(0f, 1f)] public float baseLight = 0.25f;

		[Header("Wind")]
		[Tooltip("Axis the layer rotates about. Tilted off the pole so clouds cross latitudes rather " +
			"than sliding along them forever.")]
		public Vector3 windAxis = new Vector3(0.15f, 1f, 0.1f);

		[Tooltip("Degrees per unit of scene time. Rotating the sample point rather than scrolling " +
			"UVs, which would tear at the poles.")]
		public float windSpeed = 0.15f;

		[Header("Cost")]
		[Tooltip("Full shades every pixel; Half shades a quarter of them and upsamples with the same " +
			"depth-aware filter the volumetric uses. The filter is shared code, so the gap between " +
			"Full and Half means the same thing for both renderers.")]
		public CostMode costMode = CostMode.Full;

		[Range(0f, 64f)] public float depthRejection = 8f;

		public DebugMode debugMode = DebugMode.Off;

		// Elapsed scene time, accumulated rather than read from Time.time, so the benchmark's fixed
		// captureDeltaTime advances the weather at the same rate a real frame would - and so that
		// pausing with P freezes the clouds, as it does for the volumetric.
		float elapsed;

		// Resolved lazily and cached: this is a ScriptableObject, so it cannot serialize scene
		// references.
		SolarSystem.SolarSystemManager solarSystem;
		Transform planet;
		Light sun;

		/// <summary>Whichever of the deck's two textures the current source selects.</summary>
		public Texture2D TextureFor(LayerSettings layer)
		{
			if (layer == null) { return null; }
			return textureSource == TextureSource.Baked ? layer.baked : layer.authored;
		}

		/// <summary>True when the upper deck is both wanted and available.</summary>
		public bool UpperActive => upperLayerEnabled && TextureFor(upper) != null;

		// Logged once rather than every frame, and reset whenever the effect is re-enabled.
		bool warnedMissingLower;
		bool warnedMissingUpper;

		/// <summary>
		/// Finds the shader by name if the field is empty.
		///
		/// The base class leaves `shader` to be assigned by hand, which is right for a generic
		/// effect but not for this one: there is exactly one shader it can ever use. Left unassigned
		/// it falls back to Unlit/Texture and the effect silently does nothing, which is precisely
		/// how this first failed to appear. An explicit assignment still wins - this only fills a
		/// hole.
		/// </summary>
		public override void OnEnable()
		{
			if (shader == null) { shader = Shader.Find("Hidden/BaselineClouds"); }
			warnedMissingLower = false;
			warnedMissingUpper = false;
			base.OnEnable();
		}

		protected override void RenderEffectToTarget(RenderTexture source, RenderTexture target)
		{
			Texture2D lowerTex = TextureFor(lower);
			if (lowerTex == null)
			{
				// Nothing baked or nothing wired: pass the frame through untouched rather than
				// drawing something misleading.
				//
				// Said out loud, because a silent passthrough is indistinguishable from the effect
				// not being in the chain at all - and an enabled renderer that draws nothing is the
				// one failure that looks exactly like success in a comparison this project keeps
				// making by eye.
				Warn(ref warnedMissingLower, "lower");
				Graphics.Blit(source, target);
				return;
			}

			// Wanted but unassigned is worth saying too: it silently halves the renderer, and a
			// missing depth cue is exactly the sort of thing that gets written up as a property of
			// the technique.
			if (upperLayerEnabled && TextureFor(upper) == null) { Warn(ref warnedMissingUpper, "upper"); }

			Advance();

			int divisor = costMode == CostMode.Half ? 2 : 1;
			int width = Mathf.Max(1, source.width / divisor);
			int height = Mathf.Max(1, source.height / divisor);

			SetProperties(height);

			// Half format for the same reason the volumetric uses one: a lit cloud top is not
			// bounded by 1, and an 8-bit target would clip it flat.
			RenderTexture cloudTex = RenderTexture.GetTemporary(
				width, height, 0, RenderTextureFormat.ARGBHalf);
			cloudTex.filterMode = FilterMode.Bilinear;

			Graphics.Blit(source, cloudTex, material, 0);

			material.SetTexture("_CloudTex", cloudTex);
			material.SetVector("_CloudTexSize", new Vector4(width, height, 1f / width, 1f / height));
			material.SetFloat("_CloudUpsample", divisor);
			material.SetFloat("_CloudDepthRejection", depthRejection);
			material.SetFloat("_CloudDebugRegion", -1f);

			Graphics.Blit(source, target, material, 1);

			RenderTexture.ReleaseTemporary(cloudTex);
		}

		/// <summary>
		/// Advances the drift on the SCENE's clock, not the wall clock: paused means paused, and
		/// scrubbing time forward at 64x carries the clouds with it rather than leaving them
		/// crawling. Falls back to real time if there is no solar system in the scene.
		/// </summary>
		void Advance()
		{
			if (solarSystem == null) { solarSystem = FindFirstObjectByType<SolarSystem.SolarSystemManager>(); }
			float timeScale = solarSystem == null ? 1f : (solarSystem.animate ? solarSystem.timeMultiplier : 0f);
			elapsed += Time.deltaTime * timeScale;
		}

		void SetProperties(int passHeight)
		{
			EnsureGradients();

			BindLayer("A", lower);
			// The upper deck's uniforms are bound whether or not it is drawn - a stale texture bound
			// to an unused sampler costs nothing, whereas leaving it unbound after a toggle would
			// leave whatever was there last, which is the class of bug an omitted benchmark toggle
			// produces.
			BindLayer("B", upper);
			material.SetInt("baselineLayerCount", UpperActive ? 2 : 1);

			material.SetInt("baselineCloudDebugMode", (int)debugMode);

			Vector3 planetCentre = ObserverGeometry.PlanetCentre(ref planet);
			Vector3 dirToSun = ObserverGeometry.DirectionToSun(ref sun);
			Vector3 observer = cam != null ? cam.transform.position : Vector3.zero;
			float sunElevation01 = ObserverGeometry.SunElevation01(observer, planetCentre, dirToSun);

			material.SetVector("baselineCloudSunDir", dirToSun);
			material.SetColor("baselineCloudSunColour", sunColour.Evaluate(sunElevation01));
			material.SetColor("baselineCloudAmbientColour", ambientColour.Evaluate(sunElevation01));
			material.SetFloat("baselineCloudSunIntensity", sunIntensity);
			material.SetFloat("baselineCloudAmbientIntensity", ambientIntensity);
			material.SetFloat("baselineCloudWrap", wrap);
			material.SetFloat("baselineCloudBaseLight", baseLight);

			// Radians one screen pixel of THIS pass subtends. The mip footprint is derived from it,
			// so it has to follow the pass resolution rather than the frame's - at Half the pixels
			// are twice as wide and the layer has to be read one mip coarser to match.
			float fov = cam != null ? cam.fieldOfView : 60f;
			float pixelAngle = 2f * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, passHeight);
			material.SetFloat("baselineCloudPixelAngle", pixelAngle);
		}

		/// <summary>
		/// One deck's uniforms, under the shader's A or B suffix.
		///
		/// A texture width of zero would put the mip footprint at infinity, so an unassigned deck
		/// falls back to a sane one rather than reading black.
		/// </summary>
		void BindLayer(string suffix, LayerSettings layer)
		{
			Texture2D tex = TextureFor(layer);

			material.SetTexture("BaselineCloudLayer" + suffix, tex);
			material.SetFloat("baselineLayerRadius" + suffix, bodyRadius + layer.altitude);
			material.SetFloat("baselineLayerThickness" + suffix, layer.thickness);
			material.SetFloat("baselineLayerOpacity" + suffix, layer.opacity);
			material.SetFloat("baselineLayerContrast" + suffix, layer.contrast);
			material.SetFloat("baselineLayerRelief" + suffix, layer.reliefStrength);
			material.SetFloat("baselineLayerTexels" + suffix, tex != null ? tex.width : 2048);
			material.SetMatrix("baselineLayerDrift" + suffix, DriftMatrix(layer.windScale));
		}

		/// <summary>Once per enable, not once per frame.</summary>
		void Warn(ref bool alreadyWarned, string which)
		{
			if (alreadyWarned) { return; }
			alreadyWarned = true;

			Debug.LogWarning($"[Baseline clouds] '{name}' is enabled but its {which} deck's " +
				$"{textureSource} texture is unassigned, so that deck is drawing nothing. Assign " +
				"it on the effect asset, or bake it from Baseline Cloud Settings.", this);
		}

		/// <summary>
		/// World space into the layer's texture space. A rotation about a tilted axis, so the whole
		/// pattern moves rigidly - a UV scroll would slide features along latitude lines and tear
		/// them at the poles, which is the mistake the volumetric's weather map already documents.
		///
		/// The two decks must get DIFFERENT scales here. Drifting at the same rate they move
		/// rigidly together and never separate, and the separation is the whole parallax.
		/// </summary>
		Matrix4x4 DriftMatrix(float speedScale)
		{
			Vector3 axis = windAxis.sqrMagnitude > 1e-6f ? windAxis.normalized : Vector3.up;
			return Matrix4x4.Rotate(Quaternion.AngleAxis(elapsed * windSpeed * speedScale, axis));
		}

		/// <summary>
		/// Fills in sensible ramps the first time, so the effect does not silently light the clouds
		/// with Unity's default black-to-white gradient.
		///
		/// The sun keys follow AerialPerspectiveSimple's haze ramp, which in turn matches the sky
		/// gradient's horizon anchors - the clouds have to be lit by the same sun the horizon they
		/// sit against is. The ambient keys are the sky's own colour at that elevation, dimmer,
		/// because that is what skylight on a cloud base actually is.
		/// </summary>
		void EnsureGradients()
		{
			if (sunColour == null || sunColour.colorKeys == null || sunColour.colorKeys.Length < 2)
			{
				sunColour = new Gradient();
				sunColour.SetKeys(
					new[]
					{
						new GradientColorKey(new Color(0.000f, 0.000f, 0.000f), 0.000f),   // -90 deg
						new GradientColorKey(new Color(0.020f, 0.020f, 0.035f), 0.470f),   //  -5
						new GradientColorKey(new Color(0.850f, 0.400f, 0.180f), 0.500f),   //   0
						new GradientColorKey(new Color(1.000f, 0.780f, 0.580f), 0.556f),   //  10
						new GradientColorKey(new Color(1.000f, 0.960f, 0.910f), 0.667f),   //  30
						new GradientColorKey(new Color(1.000f, 1.000f, 1.000f), 1.000f)    //  90
					},
					new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
			}

			if (ambientColour == null || ambientColour.colorKeys == null || ambientColour.colorKeys.Length < 2)
			{
				ambientColour = new Gradient();
				ambientColour.SetKeys(
					new[]
					{
						// Not black at t=0: every source a cloud has would otherwise reach zero
						// together and the deck would turn pure black rather than dark. Declared
						// non-physical, exactly like the ocean's and the land's night terms.
						new GradientColorKey(new Color(0.030f, 0.035f, 0.055f), 0.000f),   // -90 deg
						new GradientColorKey(new Color(0.080f, 0.090f, 0.140f), 0.470f),   //  -5
						new GradientColorKey(new Color(0.300f, 0.230f, 0.230f), 0.500f),   //   0
						new GradientColorKey(new Color(0.420f, 0.450f, 0.520f), 0.556f),   //  10
						new GradientColorKey(new Color(0.480f, 0.560f, 0.680f), 0.667f),   //  30
						new GradientColorKey(new Color(0.500f, 0.580f, 0.700f), 1.000f)    //  90
					},
					new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
			}
		}
	}
}
