using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Clouds
{
	/// <summary>
	/// Volumetric clouds on a spherical shell, following Schneider (Guerrilla).
	///
	/// A post-process rather than a CommandBuffer pass: RenderingManager.Setup calls
	/// cam.RemoveAllCommandBuffers, so a buffer added from anywhere else is silently wiped on the
	/// next enable, and SkyPass.Record is asserted to be the only sky-pass shape. The post-process
	/// chain is the sanctioned extension point and already has the depth texture.
	///
	/// Sits AFTER the atmosphere composite in the chain, so the clouds do not inherit its aerial
	/// perspective and apply their own instead - see cloudAerialStrength and aerialTransmittance in
	/// SurfaceLighting.hlsl. They cannot inherit it: that pass reads a screen-space LUT whose depth
	/// axis is capped at the planet's tangent distance, and the clouds that most need haze are the
	/// ones past that cap.
	/// </summary>
	[CreateAssetMenu(menuName = "PostProcessing/Clouds")]
	public class CloudEffect : PostProcessingEffect
	{
		/// <summary>
		/// What the march costs, as a switchable mode over one kernel rather than as separate
		/// renderers - so RQ2 gets a curve across a technique instead of a single number, and the
		/// comparison between the points on it is not confounded by two implementations.
		/// </summary>
		public enum CostMode
		{
			Full,
			Half,
			Temporal,
		}

		/// <summary>
		/// The ten genera of THESIS.md 3.5, which is RQ1's scoring rubric.
		///
		/// Ordered by altitude band - low, mid, high - because "are they recognisable at the altitude
		/// band they belong to" is half of what the rubric asks, and reading the list in band order
		/// makes a wrong placement obvious.
		/// </summary>
		public enum Genus
		{
			Off = 0,

			Stratus,
			Stratocumulus,
			Cumulus,
			Cumulonimbus,

			Altostratus,
			Altocumulus,
			Nimbostratus,

			Cirrus,
			Cirrocumulus,
			Cirrostratus,
		}

		public enum DebugMode
		{
			Off = 0,
			Segment = 3,
			Density = 4,
			StartDistance = 5,
			CameraRegion = 6,
			Coverage = 7,
			CloudType = 8,
			Precipitation = 9,
			BaseHeight = 10,
		}

		[Header("Baked volumes")]
		public Texture3D shapeNoise;
		public Texture3D detailNoise;

		[Header("Weather map")]
		public ComputeShader weatherCompute;
		public Vector2Int weatherMapSize = new Vector2Int(512, 256);
		[Range(1, 8)] public int weatherOctaves = 5;
		[Range(1.2f, 4f)] public float weatherLacunarity = 2f;
		[Range(0.1f, 0.9f)] public float weatherGain = 0.5f;

		[Tooltip("Feature size of the coverage field. Lower means larger weather systems.")]
		[Range(0.2f, 12f)] public float coverageScale = 2.2f;
		[Tooltip("The main how-cloudy-is-it dial.")]
		[Range(-0.5f, 0.5f)] public float coverageBias = 0f;
		[Tooltip("Broken versus overcast: higher pushes coverage toward all-or-nothing.")]
		[Range(0.2f, 4f)] public float coverageContrast = 1.3f;

		[Tooltip("Stretches every weather field to fill its range before it is folded to 0-1. An FBM " +
			"normalised by the sum of its amplitudes never reaches that sum, so without this each " +
			"field sat in the middle third of its range and the whole globe had nearly the same " +
			"weather - cumulus almost everywhere, with stratus and cumulonimbus unreachable. The " +
			"default is measured against the current octave counts; re-check it if those change.")]
		[Range(1f, 4f)] public float weatherRangeGain = 2.3f;

		[Tooltip("Cloud type varies on its own, much larger scale - a region can be solidly covered " +
			"in flat stratus or lightly dotted with cumulus, and tying type to coverage would " +
			"collapse the genera axis RQ1 scores.")]
		[Range(0.1f, 6f)] public float typeScale = 1.1f;
		[Range(-0.5f, 0.5f)] public float typeBias = 0f;
		[Tooltip("Pushes cloud type toward the extremes, the same way coverage contrast does. This " +
			"is what makes stratus and cumulonimbus regions reachable rather than everything " +
			"tending to cumulus - which RQ1 scores directly.")]
		[Range(0.2f, 4f)] public float typeContrast = 1.6f;

		[Range(0.2f, 12f)] public float precipitationScale = 3.5f;
		[Range(-0.5f, 0.5f)] public float precipitationBias = -0.1f;

		[Tooltip("Feature size of the height field that lifts and lowers the cloud band. Low values " +
			"vary it continentally, which reads as no variation at all when looking at one group of " +
			"clouds; around 12-20 varies it across a few clouds, which is the scale the eye reads as " +
			"some being taller than others. Past about 30 it falls below what a 512-wide map can " +
			"resolve and turns to mush.")]
		[Range(0.2f, 30f)] public float baseHeightScale = 16f;
		[Range(-0.5f, 0.5f)] public float baseHeightBias = 0f;

		[Tooltip("Speed the weather field drifts, in the sphere's own space rather than as a UV " +
			"scroll - a scroll would slide features along latitude lines and tear them at the poles.")]
		public float weatherWindSpeed = 0.004f;

		[Header("High cloud weather")]
		[Tooltip("The ice layer's own coverage field. Its own scales because high cloud arrives on " +
			"its own weather - a cirrus shield runs ahead of a front and has nothing to do with the " +
			"cumulus underneath it. Tying them would collapse the band axis RQ1 scores.")]
		[Range(0.2f, 12f)] public float highCoverageScale = 1.6f;

		[Tooltip("Cirrus is usually sparse, so this sits negative by default.")]
		[Range(-0.5f, 0.5f)] public float highCoverageBias = -0.15f;

		[Range(0.2f, 4f)] public float highCoverageContrast = 1.4f;

		[Tooltip("Which of the three ice genera this region tends toward: fibrous Cirrus, granular " +
			"Cirrocumulus, or a Cirrostratus veil.")]
		[Range(0.1f, 6f)] public float highFormScale = 0.9f;
		[Range(-0.5f, 0.5f)] public float highFormBias = 0f;

		[Header("Shell")]
		[Tooltip("Sea level, in world units. Its own field rather than the atmosphere's planetRadius " +
			"global, which is zero whenever the atmosphere is off - and the clouds have to keep " +
			"working in the baseline and ablation profiles.")]
		public float bodyRadius = 150f;

		[Tooltip("Cloud base above sea level, world units. At the scene's scale one unit is about " +
			"0.91 km, so the low cloud band that THESIS.md makes RQ1's rubric - below 2000 m - is " +
			"under 2.2 units.")]
		public float cloudBottomAltitude = 1f;
		public float cloudTopAltitude = 10f;

		[Header("Density")]
		[Range(0.001f, 0.5f)] public float shapeScale = 0.06f;
		[Range(0.01f, 4f)] public float detailScale = 0.5f;
		[Range(0f, 1f)] public float detailWeight = 0.35f;
		[Range(0f, 8f)] public float densityMultiplier = 1.5f;
		[Range(0f, 3f)] public float coverageMultiplier = 1f;
		[Range(-0.5f, 0.5f)] public float typeOffset = 0f;

		[Tooltip("How far the cloud band is lifted and lowered across the globe, as a fraction of " +
			"the shell. This is what makes some clouds reach higher than others.")]
		[Range(0f, 0.6f)] public float baseVariation = 0.3f;

		[Tooltip("Thickness of the cloud band, as a fraction of the shell. The band moves up and " +
			"down within the shell rather than stretching to fill it - stretching is what pinned " +
			"every cloud top to the same height. Base variation plus this should stay under 1.")]
		[Range(0.1f, 1f)] public float bandThickness = 0.65f;

		[Tooltip("How much of the height gradient acts on coverage rather than density. 0 is " +
			"Schneider's arrangement, where the layer stops at a flat plane once the gradient falls " +
			"below 1 - coverage - halfway down the ramp at coverage 0.5. Higher thins the top and " +
			"bottom into scattered cloud instead of ending them at a surface.")]
		[Range(0f, 1f)] public float edgeSoftness = 0.7f;

		[Header("High band - the ice layer")]
		[Tooltip("Cirrus, Cirrocumulus and Cirrostratus - three of the ten genera THESIS.md makes " +
			"RQ1's rubric, and the three the single low layer could never reach. 0 removes the layer " +
			"exactly, so its cost and its contribution can both be priced against one build.")]
		[Range(0f, 1f)] public float highStrength = 1f;

		[Tooltip("Where the ice layer's floor sits, as a fraction of the shell. The high band starts " +
			"at 6000 m; at 0.91 km per world unit and a shell running 0.91 to 9.1 km, that is 0.64 - " +
			"so the default puts it in the band the taxonomy names rather than merely near the top.")]
		[Range(0f, 0.95f)] public float highBase = 0.64f;

		[Tooltip("Thin, because cirrus is. A thick ice layer reads as high fog rather than as cirrus.")]
		[Range(0.02f, 0.4f)] public float highThickness = 0.22f;

		[Range(0.001f, 0.5f)] public float highScale = 0.05f;

		[Tooltip("Far lower than the low layer's. Cirrus is optically thin - the sun is visible " +
			"through it, and that translucency is most of what identifies it.")]
		[Range(0f, 2f)] public float highDensityMultiplier = 0.28f;

		[Range(0f, 3f)] public float highCoverageMultiplier = 1f;

		[Tooltip("How far the sample coordinate is stretched along the wind for the fibrous form. " +
			"This is what makes cirrus read as ice rather than as thin cumulus - the streaks are the " +
			"visual signature of the genus, and an unstretched sample gives faint puffs instead.")]
		[Range(1f, 16f)] public float highStretch = 7f;

		[Tooltip("How far the granular form leans on the volume's cellular channels. Cirrocumulus is " +
			"a field of small cells, which is what the Worley FBM already describes.")]
		[Range(0f, 1f)] public float highGranularity = 0.8f;

		[Tooltip("Direction the fibres run, and the direction the ice layer drifts. High cloud sits " +
			"in a different wind from the deck below it, so this is its own.")]
		public Vector3 highWindDirection = new Vector3(1f, 0f, 0.35f);
		public float highWindSpeed = 0.05f;

		[Header("Precipitation")]
		[Tooltip("Rain-bearing cloud is thicker, taller and hangs lower. One field drives all three, " +
			"so a storm region reads as a storm rather than as three unrelated patterns.")]
		[Range(0f, 3f)] public float precipDensity = 1.2f;
		[Range(0f, 1f)] public float precipTypePush = 0.45f;
		[Range(0f, 0.6f)] public float precipBaseDrop = 0.2f;

		[Header("Wind")]
		public Vector3 shapeWindDirection = new Vector3(1f, 0.1f, 0.2f);
		public float shapeWindSpeed = 0.02f;
		public float detailWindSpeed = 0.05f;

		[Header("March")]
		[Tooltip("Target world-unit length of one step. The step COUNT follows from the segment, so " +
			"a vertical ray through the shell and a grazing ray through a long chord are both " +
			"sampled properly. The reference project instead hardcodes a step of 11 world units, " +
			"which here would step over the whole layer six times over in one step.")]
		[Range(0.005f, 1f)] public float stepSize = 0.05f;

		[Range(16, 512)] public int maxSteps = 192;

		[Tooltip("Diagnostic overlay for the march itself, drawn instead of the clouds. Step Size " +
			"and Step Count show how the sampling rate varies across the frame; Segment shows the " +
			"length of shell each ray crosses; Density shows accumulated density before any " +
			"lighting. If an artefact tracks one of these, that is where it comes from.")]
		public DebugMode debugMode = DebugMode.Off;
		[Range(0f, 2f)] public float jitterStrength = 1f;
		[Range(0.1f, 20f)] public float extinction = 4f;

		[Header("Lighting")]
		[Tooltip("Scales the direct sun term. The sun's COLOUR is physical - it comes from the " +
			"atmosphere's transmittance LUT at each sample's own altitude - but its absolute level " +
			"is authored, because the march works in display space alongside an already tone-mapped " +
			"background rather than in radiance.")]
		[Range(0f, 8f)] public float sunIntensity = 2.2f;

		[Tooltip("How much of the sun's atmospheric extinction to take. This atmosphere is optically " +
			"far thicker than Earth's - zenith transmittance 0.45 against 0.77 - so at 1 even a midday " +
			"sun reaches a cloud top already dimmed to 45% and reddened, which reads as grey. Lerping " +
			"toward white keeps the colour physical while letting the level be authored, the same way " +
			"the ocean glint's transmittance weight does.")]
		[Range(0f, 1f)] public float sunTransmittanceWeight = 0.7f;

		[Tooltip("Scales skylight on the clouds, from the same sky-view LUT the ocean and land use. " +
			"At low sun this does more of the work than the direct term - it is what puts the sunset " +
			"on the undersides. In daylight it must stay well under the direct term: at parity the " +
			"whole cloud sits at mid-grey with barely any top-to-base gradient, which is exactly how " +
			"a flat, shaded look happens.")]
		[Range(0f, 4f)] public float ambientIntensity = 0.4f;

		[Tooltip("How much of the skylight is taken from the sunlit horizon rather than the zenith. " +
			"At sunset the zenith is deep blue and the horizon is orange, so a zenith-only lookup " +
			"throws away the colour the clouds should be picking up.")]
		[Range(0f, 1f)] public float ambientHorizon = 0.6f;

		[Tooltip("Stands in for skylight when there is no physically based atmosphere bound, so the " +
			"clouds are still lit in the baseline and ablation profiles rather than going black.")]
		public Color ambientFallback = new Color(0.4f, 0.45f, 0.55f);

		[Tooltip("How much aerial perspective the clouds pick up - the haze of the air between them " +
			"and the camera. Distant cloud loses contrast and takes the colour of the sky it is seen " +
			"against, which is what stops it reading as crisp cut-outs over terrain that has already " +
			"gone blue. 0 is identity, for pricing the feature.")]
		[Range(0f, 1f)] public float aerialStrength = 1f;

		[Tooltip("Starlight and airglow on the clouds. Without it they go pure black at night, " +
			"because every source they have reaches zero together once the sun is down. Declared " +
			"non-physical, like the ocean's and the land's night terms.")]
		public Color nightAmbient = new Color(0.03f, 0.035f, 0.05f);

		[Header("Light march")]
		[Tooltip("How far toward the sun the light march reaches, in world units. Around one shell " +
			"thickness is usually right - beyond that it is sampling air.")]
		[Range(0.1f, 8f)] public float lightMarchLength = 1.6f;
		[Range(0.1f, 8f)] public float lightAbsorption = 2.5f;
		[Tooltip("How far the light march spreads into a cone. A straight line makes every cloud " +
			"self-shadow like a slab; the cone is what lets light wrap around a billow.")]
		[Range(0f, 1f)] public float coneSpread = 0.25f;

		[Tooltip("How finely the light march reads the cloud, in world units - separate from how far " +
			"it steps. The step has to be long to cross a cloud in six samples, but choosing the mip " +
			"from that length samples a volume blurred past the point where a lit face differs from " +
			"a shadowed one, which erases self-shadowing entirely. Lower is sharper; raise it if the " +
			"shadowing looks noisy.")]
		[Range(0.02f, 2f)] public float lightMarchDetail = 0.15f;
		[Tooltip("The dark edge that comes from light having to scatter into a thin volume before it " +
			"can leave. Keep it low: the powder factor rises from zero with optical depth, so at high " +
			"strength it darkens the cloud TOP most - the part with nothing between it and the sun - " +
			"and inverts the top-to-base shading. Past about 0.45 the tops go darker than the bases. " +
			"The reference project omits this term entirely.")]
		[Range(0f, 1f)] public float powderStrength = 0.25f;

		[Header("Phase")]
		[Tooltip("Forward lobe. Softer than the reference project's 0.8, because that value 's " +
			"peak is roughly twenty times its sideways value - which only became visible once the " +
			"phase was scaled into the right units.")]
		[Range(0f, 0.99f)] public float phaseForward = 0.45f;
		[Range(0f, 0.99f)] public float phaseBackward = 0.2f;
		[Range(0f, 1f)] public float phaseBlend = 0.5f;

		[Tooltip("Silver lining: how many times the isotropic value the phase reaches looking " +
			"straight at the sun. A separate tight forward lobe combined with max(), not blended - " +
			"two lobes alone cannot produce this, so it is an addition to the model rather than a " +
			"tuning of it. 0 removes it.")]
		[Range(0f, 24f)] public float silverIntensity = 6f;

		[Tooltip("How wide the silver lining is. 0.05 gives a rim within about 4 degrees of the sun, " +
			"0.25 about 13, 0.3 about 22.")]
		[Range(0.02f, 0.6f)] public float silverSpread = 0.25f;

		[Header("Moon")]
		[Tooltip("Scales moonlight on the clouds. The moon's published colour is already scaled by " +
			"its phase, so a new moon contributes nothing and costs nothing - the whole term sits " +
			"behind a branch on it. 0 disables it outright.")]
		[Range(0f, 8f)] public float moonIntensity = 2f;

		[Tooltip("Silver lining from the moon. Weaker than the sun's, since the moon is a far dimmer " +
			"source, but the same tight forward lobe - it is what makes a cloud edge crossing the " +
			"moon read as lit rather than as a silhouette.")]
		[Range(0f, 24f)] public float moonSilverIntensity = 2f;

		[Header("Sky under the deck")]
		[Tooltip("What the cloud base looks like from below, and how much sky it hides, as an " +
			"equirectangular field. Without it the ocean and the terrain darken their direct sun " +
			"under cloud but still reflect and are lit by a CLEAR sky - which is why an overcast " +
			"sunset still turned the sea orange.")]
		public ComputeShader skyCompute;

		[Tooltip("Lower than the shadow map, because this pass is the expensive one: every vertical " +
			"step pays for a six-sample cone light march. A cloud base carries no high-frequency " +
			"detail, so it can afford to be coarse.")]
		public Vector2Int skyMapSize = new Vector2Int(256, 128);

		[Range(2, 32)] public int skySteps = 8;

		[Tooltip("Widens the footprint the density is read at, relative to what this map can " +
			"represent. Same control and same reason as the shadow map's - reading finer detail " +
			"than a fixed global grid can carry aliases, and the drift makes it crawl.")]
		[Range(0.5f, 4f)] public float skyDetailScale = 1f;

		[Tooltip("How strongly cloud replaces the sky the surfaces see. 0 is identity, which is what " +
			"prices the feature; it is also what happens when the clouds are off.")]
		[Range(0f, 1f)] public float skyStrength = 1f;

		[Header("Genus override")]
		[Tooltip("Pins every weather field to one genus, globally. Authoring scaffolding rather than " +
			"a rendering path - it feeds the same density model, so what it shows is genuinely what " +
			"the renderer can produce. RQ1 needs one image of each of the ten, and hunting for a " +
			"region where the noise happened to make Cirrocumulus is not a workflow.")]
		public Genus genusOverride = Genus.Off;

		[Header("Cost")]
		[Tooltip("Full marches every pixel - the honest upper bound and the cleanest image. Half " +
			"marches a quarter of the pixels and upsamples with a depth-aware filter. Both are the " +
			"same march at different resolutions, so the difference between them is a measurement " +
			"of the technique rather than of two implementations.")]
		public CostMode costMode = CostMode.Half;

		[Tooltip("How hard the upsample rejects a neighbour across a depth discontinuity. Higher " +
			"keeps silhouettes crisp and lets more low-resolution stair-stepping through; lower " +
			"smooths the stepping and bleeds cloud across edges.")]
		[Range(0f, 64f)] public float depthRejection = 8f;

		[Tooltip("Temporal only. How much of a freshly marched pixel to take each frame. Lower is " +
			"steadier but slower to respond, so a fast camera pan leaves more of a trail.")]
		[Range(0.05f, 1f)] public float historyBlend = 0.2f;

		[Header("Shadows on the ground")]
		public ComputeShader shadowCompute;
		[Tooltip("Equirectangular, so it needs no shadow frustum and does not change with the " +
			"camera. Modest resolution is enough: a shadow cast from a kilometre up carries no " +
			"high-frequency detail anyway.")]
		public Vector2Int shadowMapSize = new Vector2Int(512, 256);
		[Range(2, 32)] public int shadowSteps = 10;
		[Range(0.1f, 8f)] public float shadowAbsorption = 1.2f;

		[Tooltip("Widens the footprint each shadow sample is read at, relative to what the shadow " +
			"map can actually represent. 1 matches the map exactly and is what stops the shadows " +
			"flickering; lower reads finer detail than the map can carry, which aliases, and the " +
			"aliasing moves with the wind. Higher softens the shadows further.")]
		[Range(0.5f, 4f)] public float shadowDetailScale = 1f;
		[Tooltip("0 disables cloud shadows entirely, which is also what happens when this effect is " +
			"disabled - so a clouds-off benchmark profile gets an unshadowed ground for free.")]
		[Range(0f, 1f)] public float shadowStrength = 0.85f;

		[Tooltip("Light shafts: how strongly the deck shadows the AIR, not just the ground. Reads the " +
			"same shadow map, but the atmosphere's raymarch applies it per step, which is what turns " +
			"in-scattered light into beams. Separate from the shadow strength above because the two " +
			"cost wildly different amounts - one tap per pixel against one tap per march step - and " +
			"RQ2 wants them priced apart. 0 is exactly identity.")]
		[Range(0f, 1f)] public float shaftStrength = 1f;

		RenderTexture weatherMap;
		RenderTexture weatherMapHigh;
		RenderTexture shadowMap;
		RenderTexture skyMap;

		// Whether the shadow globals currently belong to THIS effect - see RenderMaps.
		bool publishedShadows;
		bool publishedSky;

		// Ping-ponged: this frame resolves into one while reading the other.
		readonly RenderTexture[] history = new RenderTexture[2];
		int historyIndex;
		bool historyValid;
		int temporalFrame;
		Matrix4x4 previousViewProjection = Matrix4x4.identity;

		// Elapsed time, accumulated rather than read from Time.time, so the benchmark's fixed
		// captureDeltaTime advances the weather at the same rate a real frame would.
		float elapsed;

		// Resolved by tag, the same way AtmosphereEffect finds it - this is a ScriptableObject, so
		// it cannot hold a scene reference.
		Light sunLight;

		// Same reason, found once rather than every frame. The clouds drift on the scene's clock so
		// that pausing time with P freezes them too - otherwise a parameter cannot be judged
		// without the thing it is being judged on moving under it.
		SolarSystem.SolarSystemManager solarSystem;

		public RenderTexture WeatherMap => weatherMap;

		public override void OnEnable()
		{
			base.OnEnable();
			// Both maps are generated at pre-cull, not in OnRenderImage, because the terrain and
			// ocean sample the shadow map during forward opaque - which has already happened by the
			// time a post-process runs. Generating it there would shadow the ground with last
			// frame's clouds. AtmosphereEffect registers its LUT dispatches the same way.
			Camera.onPreCull -= RenderMaps;
			Camera.onPreCull += RenderMaps;
		}

		public override void OnDestroy()
		{
			Camera.onPreCull -= RenderMaps;
			ReleaseWeatherMap();
			ReleaseShadowMap();
			ReleaseSkyMap();
			ReleaseHistory();
		}

		void ReleaseHistory()
		{
			for (int i = 0; i < history.Length; i++)
			{
				if (history[i] == null) { continue; }
				history[i].Release();
				DestroyImmediate(history[i]);
				history[i] = null;
			}
			historyValid = false;
		}

		/// <summary>Full-resolution accumulation buffers, recreated when the frame size changes.</summary>
		void EnsureHistory(int width, int height)
		{
			if (history[0] != null && history[0].width == width && history[0].height == height) { return; }

			ReleaseHistory();
			for (int i = 0; i < history.Length; i++)
			{
				history[i] = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf)
				{
					filterMode = FilterMode.Bilinear,
					wrapMode = TextureWrapMode.Clamp,
					name = $"Cloud History {i}"
				};
				history[i].Create();
			}
		}

		void ReleaseSkyMap()
		{
			if (skyMap == null) { return; }
			skyMap.Release();
			DestroyImmediate(skyMap);
			skyMap = null;
		}

		/// <summary>
		/// The deck's underside, into the globals the surface shaders read.
		///
		/// Same shape as RenderShadowMap, and it retracts only what it published for the same reason:
		/// both cloud renderers keep a pre-cull callback registered whether or not they are enabled,
		/// so an unconditional zero would let the disabled one wipe the enabled one's field.
		/// </summary>
		void RenderSkyMap()
		{
			if (!enabled || skyCompute == null || skyStrength <= 0f)
			{
				if (publishedSky)
				{
					publishedSky = false;
					Shader.SetGlobalFloat("cloudSkyStrength", 0f);
				}
				return;
			}

			int width = Mathf.Max(8, skyMapSize.x);
			int height = Mathf.Max(8, skyMapSize.y);

			if (skyMap == null || !skyMap.IsCreated() ||
				skyMap.width != width || skyMap.height != height)
			{
				ReleaseSkyMap();
				// Half float, not RGBA8: a lit deck is not bounded by 1 and an 8-bit target would
				// clip it flat - the same reason the cloud march's own target is ARGBHalf.
				skyMap = new RenderTexture(width, height, 0, GraphicsFormat.R16G16B16A16_SFloat)
				{
					enableRandomWrite = true,
					wrapModeU = TextureWrapMode.Repeat,   // longitude wraps
					wrapModeV = TextureWrapMode.Clamp,    // latitude does not
					filterMode = FilterMode.Bilinear,
					name = "Cloud Sky Map"
				};
				skyMap.Create();
			}

			ResolveSun();
			Vector3 sunDir = sunLight != null ? -sunLight.transform.forward : Vector3.up;

			int kernel = skyCompute.FindKernel("CSCloudSkyMap");
			BindDensity(skyCompute, kernel);
			skyCompute.SetTexture(kernel, "Result", skyMap);
			skyCompute.SetVector("cloudSkyMapSize", new Vector2(width, height));
			skyCompute.SetVector("cloudSkySunDir", sunDir);
			skyCompute.SetInt("cloudSkySteps", skySteps);
			skyCompute.SetFloat("cloudSkyDetailScale", skyDetailScale);

			// The march's own lighting parameters, bound identically. These have to stay in step with
			// SetProperties below, exactly as BindDensity does - a divergence would read as the sea
			// reflecting a deck that does not match the one drawn above it.
			BindDeckLighting(skyCompute);

			skyCompute.Dispatch(kernel, Mathf.CeilToInt(width / 8f), Mathf.CeilToInt(height / 8f), 1);

			Shader.SetGlobalTexture("CloudSkyMap", skyMap);
			Shader.SetGlobalFloat("cloudSkyStrength", skyStrength);
			// Where the surfaces should intersect a reflected ray to find the deck. Mid-shell: the
			// map describes the whole column, so any single radius is a choice, and the middle is the
			// least wrong one for a grazing ray.
			CloudShadowGlobals.PublishDeckRadius((InnerRadius + OuterRadius) * 0.5f);
			publishedSky = true;
		}

		/// <summary>
		/// The lighting terms the deck bake shares with the view march.
		///
		/// Sets exactly the names SetProperties sets on the material, for the same reason BindDensity
		/// exists: two consumers of one model have to be handed the same numbers, and the failure of
		/// a divergence here is a plausible image rather than an obvious fault.
		/// </summary>
		void BindDeckLighting(ComputeShader compute)
		{
			float gate = Shader.GetGlobalFloat("skyReflectionStrength");
			Color sunColour = sunLight != null ? sunLight.color : Color.white;
			Color gradient = Color.Lerp(sunColour, Color.white, gate);

			compute.SetVector("cloudSunColour", new Vector4(gradient.r, gradient.g, gradient.b, 1));
			compute.SetFloat("cloudSunIntensity", sunIntensity);
			compute.SetFloat("cloudSunTransmittanceWeight", sunTransmittanceWeight);
			compute.SetFloat("cloudAmbientIntensity", ambientIntensity);
			compute.SetFloat("cloudAmbientHorizon", ambientHorizon);
			compute.SetVector("cloudAmbientFallback", new Vector4(ambientFallback.r, ambientFallback.g, ambientFallback.b, 1));
			compute.SetVector("cloudNightAmbient", new Vector4(nightAmbient.r, nightAmbient.g, nightAmbient.b, 1));
			compute.SetFloat("cloudSilverIntensity", silverIntensity);
			compute.SetFloat("cloudExtinction", extinction);

			// The optical model's own parameters, which CloudLightingCommon.hlsl owns.
			compute.SetFloat("cloudLightMarchLength", lightMarchLength);
			compute.SetFloat("cloudLightAbsorption", lightAbsorption);
			compute.SetFloat("cloudConeSpread", coneSpread);
			compute.SetFloat("cloudLightMarchDetail", lightMarchDetail);
			compute.SetFloat("cloudPowderStrength", powderStrength);
			compute.SetFloat("cloudPhaseForward", phaseForward);
			compute.SetFloat("cloudPhaseBackward", phaseBackward);
			compute.SetFloat("cloudPhaseBlend", phaseBlend);
			compute.SetFloat("cloudSilverSpread", silverSpread);
		}

		void ReleaseShadowMap()
		{
			if (shadowMap != null)
			{
				shadowMap.Release();
				DestroyImmediate(shadowMap);
				shadowMap = null;
			}
		}

		/// <summary>Weather and shadow maps, before anything opaque draws.</summary>
		void RenderMaps(Camera renderingCamera)
		{
			if (cam != null && renderingCamera != cam) { return; }

			if (!enabled || shapeNoise == null || detailNoise == null || weatherCompute == null)
			{
				// Tell the surface shaders there is nothing overhead, so the ground is unshadowed
				// whenever the clouds are off. This is what makes a clouds-off benchmark profile
				// correct without the profile having to know anything about shadows.
				//
				// Only retract what THIS effect published, though. PostProcessingManager calls
				// OnEnable on every effect in the chain regardless of its `enabled` flag, so the
				// baseline cloud renderer also has a pre-cull callback registered at all times.
				// Zeroing unconditionally would let whichever of the two is DISABLED wipe the
				// strength the enabled one had just set, with callback order deciding the winner -
				// ground shadows that flicker or never appear, and nothing wrong in either renderer.
				if (publishedShadows)
				{
					publishedShadows = false;
					CloudShadowGlobals.Retract();
				}
				if (publishedSky)
				{
					publishedSky = false;
					Shader.SetGlobalFloat("cloudSkyStrength", 0f);
				}
				return;
			}

			RenderWeatherMap();
			RenderShadowMap();
			RenderSkyMap();
		}

		void ReleaseWeatherMap()
		{
			if (weatherMap != null)
			{
				weatherMap.Release();
				DestroyImmediate(weatherMap);
				weatherMap = null;
			}
			if (weatherMapHigh != null)
			{
				weatherMapHigh.Release();
				DestroyImmediate(weatherMapHigh);
				weatherMapHigh = null;
			}
		}

		/// <summary>One weather field's target. Both layers are the same size and format.</summary>
		RenderTexture CreateWeatherTarget(int width, int height, string label)
		{
			var rt = new RenderTexture(width, height, 0, GraphicsFormat.R8G8B8A8_UNorm)
			{
				enableRandomWrite = true,
				// Longitude wraps, latitude does not - the same split the city light field uses.
				wrapModeU = TextureWrapMode.Repeat,
				wrapModeV = TextureWrapMode.Clamp,
				filterMode = FilterMode.Bilinear,
				name = label
			};
			rt.Create();
			return rt;
		}

		protected override void RenderEffectToTarget(RenderTexture source, RenderTexture target)
		{
			if (shapeNoise == null || detailNoise == null || weatherCompute == null)
			{
				// Nothing baked or nothing wired: pass the frame through untouched rather than
				// drawing something misleading.
				Graphics.Blit(source, target);
				return;
			}

			SetProperties();

			// March into an offscreen target, optionally resolve it against the previous frame, then
			// composite over the frame. Keeping all three cost modes on one march is what makes the
			// difference between them attributable to the strategy rather than to three renderers.
			//
			//   Full      every pixel, every frame
			//   Half      quarter of the pixels, upsampled with a depth-aware filter
			//   Temporal  full resolution, but a quarter of the pixels marched per frame and the
			//             rest reprojected from the previous result
			int divisor = costMode == CostMode.Half ? 2 : 1;
			int width = Mathf.Max(1, source.width / divisor);
			int height = Mathf.Max(1, source.height / divisor);

			material.SetVector("_CloudMarchSize", new Vector4(width, height, 1f / width, 1f / height));
			material.SetInt("cloudTemporalPeriod", costMode == CostMode.Temporal ? 4 : 1);
			material.SetInt("cloudTemporalIndex", temporalFrame & 3);

			// Half format: the cloud's accumulated luminance is not bounded by 1, so an 8-bit target
			// would clip the lit tops flat.
			RenderTexture cloudTex = RenderTexture.GetTemporary(
				width, height, 0, RenderTextureFormat.ARGBHalf);
			cloudTex.filterMode = FilterMode.Bilinear;

			Graphics.Blit(source, cloudTex, material, 0);

			RenderTexture resolved = cloudTex;

			if (costMode == CostMode.Temporal)
			{
				EnsureHistory(width, height);
				int previous = historyIndex;
				int current = 1 - historyIndex;

				material.SetTexture("_CloudTex", cloudTex);
				material.SetTexture("_CloudHistory", history[previous]);
				material.SetMatrix("_CloudPrevViewProj", previousViewProjection);
				material.SetFloat("_CloudHistoryBlend", historyBlend);
				material.SetFloat("_CloudHistoryValid", historyValid ? 1f : 0f);
				// Mid-shell, as the stand-in depth for reprojection - see the note in the resolve
				// pass for why the scene depth cannot be used.
				material.SetFloat("_CloudReprojectRadius", (InnerRadius + OuterRadius) * 0.5f);

				Graphics.Blit(source, history[current], material, 1);

				resolved = history[current];
				historyIndex = current;
				historyValid = true;
			}
			else
			{
				historyValid = false;
			}

			material.SetTexture("_CloudTex", resolved);
			material.SetVector("_CloudTexSize", new Vector4(width, height, 1f / width, 1f / height));
			material.SetFloat("_CloudUpsample", divisor);
			material.SetFloat("_CloudDepthRejection", depthRejection);

			Graphics.Blit(source, target, material, 2);

			RenderTexture.ReleaseTemporary(cloudTex);

			// Captured after drawing, for the next frame to reproject against. The plain projection
			// matrix rather than the GPU one: this is used to derive texture coordinates, not to
			// rasterise, so the platform's clip-space conventions are not wanted here.
			if (cam != null)
			{
				previousViewProjection = cam.projectionMatrix * cam.worldToCameraMatrix;
			}
			temporalFrame++;
		}

		void RenderWeatherMap()
		{
			int width = Mathf.Max(8, weatherMapSize.x);
			int height = Mathf.Max(8, weatherMapSize.y);

			if (weatherMap == null || !weatherMap.IsCreated() ||
				weatherMap.width != width || weatherMap.height != height)
			{
				ReleaseWeatherMap();
				weatherMap = CreateWeatherTarget(width, height, "Cloud Weather Map");
				weatherMapHigh = CreateWeatherTarget(width, height, "Cloud Weather Map High");
			}

			// Advanced on the scene's clock, not the wall clock: paused means paused, and scrubbing
			// time forward at 64x carries the weather with it rather than leaving it crawling.
			// Falls back to real time if there is no solar system in the scene.
			if (solarSystem == null) { solarSystem = FindFirstObjectByType<SolarSystem.SolarSystemManager>(); }
			float timeScale = solarSystem == null ? 1f : (solarSystem.animate ? solarSystem.timeMultiplier : 0f);

			elapsed += Time.deltaTime * timeScale;
			float weatherOffset = elapsed * weatherWindSpeed;

			int kernel = weatherCompute.FindKernel("CSWeatherMap");
			weatherCompute.SetTexture(kernel, "Result", weatherMap);
			// One dispatch writes both layers, so they cannot drift out of step.
			weatherCompute.SetTexture(kernel, "ResultHigh", weatherMapHigh);
			weatherCompute.SetVector("weatherMapSize", new Vector2(width, height));
			weatherCompute.SetInt("octaves", weatherOctaves);
			weatherCompute.SetFloat("lacunarity", weatherLacunarity);
			weatherCompute.SetFloat("gain", weatherGain);
			weatherCompute.SetFloat("coverageScale", coverageScale);
			weatherCompute.SetFloat("coverageBias", coverageBias);
			weatherCompute.SetFloat("coverageContrast", coverageContrast);
			weatherCompute.SetFloat("weatherRangeGain", weatherRangeGain);
			weatherCompute.SetFloat("typeContrast", typeContrast);
			weatherCompute.SetFloat("baseHeightScale", baseHeightScale);
			weatherCompute.SetFloat("baseHeightBias", baseHeightBias);
			weatherCompute.SetFloat("typeScale", typeScale);
			weatherCompute.SetFloat("typeBias", typeBias);
			weatherCompute.SetFloat("precipitationScale", precipitationScale);
			weatherCompute.SetFloat("precipitationBias", precipitationBias);
			weatherCompute.SetFloat("highCoverageScale", highCoverageScale);
			weatherCompute.SetFloat("highCoverageBias", highCoverageBias);
			weatherCompute.SetFloat("highCoverageContrast", highCoverageContrast);
			weatherCompute.SetFloat("highFormScale", highFormScale);
			weatherCompute.SetFloat("highFormBias", highFormBias);
			weatherCompute.SetVector("windOffset", new Vector4(weatherOffset, weatherOffset * 0.3f, weatherOffset * 0.7f, 0));

			weatherCompute.Dispatch(kernel, Mathf.CeilToInt(width / 8f), Mathf.CeilToInt(height / 8f), 1);
		}

		/// <summary>Public so the baseline's capture bake can march the same shell.</summary>
		public float InnerRadius => bodyRadius + cloudBottomAltitude;
		public float OuterRadius => bodyRadius + Mathf.Max(cloudBottomAltitude + 0.01f, cloudTopAltitude);

		/// <summary>Unit direction the ice layer's fibres run along.</summary>
		Vector3 HighWindDir =>
			highWindDirection.sqrMagnitude > 1e-6f ? highWindDirection.normalized : Vector3.right;

		Vector3 HighWind => HighWindDir * (elapsed * highWindSpeed);

		Vector3 ShapeWind
		{
			get
			{
				Vector3 dir = shapeWindDirection.sqrMagnitude > 1e-6f ? shapeWindDirection.normalized : Vector3.right;
				return dir * (elapsed * shapeWindSpeed);
			}
		}

		Vector3 DetailWind
		{
			get
			{
				Vector3 dir = shapeWindDirection.sqrMagnitude > 1e-6f ? shapeWindDirection.normalized : Vector3.right;
				return dir * (elapsed * detailWindSpeed);
			}
		}

		/// <summary>
		/// The density model's parameters, bound to a compute. The material overload below sets the
		/// same names - they have to stay in step, because the shadow pass and the march share one
		/// density function and a divergence would read as clouds not matching their own shadows.
		/// </summary>
		/// <summary>
		/// Makes the density model available to another compute - the baseline's capture bake.
		///
		/// Public for the same reason AtmosphereEffect.ApplyAtmosphereValuesTo is: the capture is
		/// only meaningful because it shares this state with the runtime renderer rather than
		/// restating it. It also generates the weather map, which the density model reads and which
		/// otherwise only exists once the effect has rendered a frame.
		///
		/// Returns false if the volumes or the weather compute are unassigned, in which case there
		/// is nothing to capture.
		/// </summary>
		public bool ApplyDensityValuesTo(ComputeShader compute, int kernel)
		{
			if (shapeNoise == null || detailNoise == null || weatherCompute == null) { return false; }

			RenderWeatherMap();
			BindDensity(compute, kernel);
			return true;
		}

		void BindDensity(ComputeShader compute, int kernel)
		{
			compute.SetTexture(kernel, "CloudShapeNoise", shapeNoise);
			compute.SetTexture(kernel, "CloudDetailNoise", detailNoise);
			compute.SetTexture(kernel, "CloudWeatherMapTex", weatherMap);
			compute.SetTexture(kernel, "CloudWeatherMapHighTex", weatherMapHigh);

			compute.SetFloat("cloudInnerRadius", InnerRadius);
			compute.SetFloat("cloudOuterRadius", OuterRadius);
			compute.SetFloat("cloudShapeScale", shapeScale);
			compute.SetFloat("cloudDetailScale", detailScale);
			compute.SetFloat("cloudDetailWeight", detailWeight);
			compute.SetFloat("cloudDensityMultiplier", densityMultiplier);
			compute.SetFloat("cloudCoverageMultiplier", coverageMultiplier);
			compute.SetFloat("cloudTypeBias", typeOffset);
			compute.SetFloat("cloudShapeResolution", shapeNoise != null ? shapeNoise.width : 128);
			compute.SetFloat("cloudDetailResolution", detailNoise != null ? detailNoise.width : 32);
			compute.SetFloat("cloudBaseVariation", baseVariation);
			compute.SetFloat("cloudBandThickness", bandThickness);
			compute.SetFloat("cloudEdgeSoftness", edgeSoftness);
			compute.SetFloat("cloudPrecipDensity", precipDensity);
			compute.SetFloat("cloudPrecipTypePush", precipTypePush);
			compute.SetFloat("cloudPrecipBaseDrop", precipBaseDrop);
			compute.SetVector("cloudShapeWind", ShapeWind);
			compute.SetVector("cloudDetailWind", DetailWind);

					compute.SetFloat("cloudHighStrength", highStrength);
			compute.SetFloat("cloudHighBase", highBase);
			compute.SetFloat("cloudHighThickness", highThickness);
			compute.SetFloat("cloudHighScale", highScale);
			compute.SetFloat("cloudHighDensityMultiplier", highDensityMultiplier);
			compute.SetFloat("cloudHighCoverageMultiplier", highCoverageMultiplier);
			compute.SetFloat("cloudHighStretch", highStretch);
			compute.SetFloat("cloudHighGranularity", highGranularity);
			compute.SetVector("cloudHighWindDir", HighWindDir);
			compute.SetVector("cloudHighWind", HighWind);
			BindGenusOverride(compute.SetFloat, compute.SetVector);
		}

		void RenderShadowMap()
		{
			if (shadowCompute == null || shadowStrength <= 0f)
			{
				if (publishedShadows)
				{
					publishedShadows = false;
					CloudShadowGlobals.Retract();
				}
				return;
			}

			int width = Mathf.Max(8, shadowMapSize.x);
			int height = Mathf.Max(8, shadowMapSize.y);

			if (shadowMap == null || !shadowMap.IsCreated() ||
				shadowMap.width != width || shadowMap.height != height)
			{
				ReleaseShadowMap();
				// RGBA8 rather than R8: random write to a single-channel 8-bit target is not
				// universally supported, and half a megabyte is not worth the compatibility risk.
				shadowMap = new RenderTexture(width, height, 0, GraphicsFormat.R8G8B8A8_UNorm)
				{
					enableRandomWrite = true,
					wrapModeU = TextureWrapMode.Repeat,   // longitude wraps
					wrapModeV = TextureWrapMode.Clamp,    // latitude does not
					filterMode = FilterMode.Bilinear,
					name = "Cloud Shadow Map"
				};
				shadowMap.Create();
			}

			ResolveSun();
			Vector3 sunDir = sunLight != null ? -sunLight.transform.forward : Vector3.up;

			int kernel = shadowCompute.FindKernel("CSCloudShadow");
			BindDensity(shadowCompute, kernel);
			shadowCompute.SetTexture(kernel, "Result", shadowMap);
			shadowCompute.SetVector("shadowMapSize", new Vector2(width, height));
			shadowCompute.SetVector("shadowSunDir", sunDir);
			shadowCompute.SetInt("shadowSteps", shadowSteps);
			shadowCompute.SetFloat("shadowAbsorption", shadowAbsorption);
			shadowCompute.SetFloat("shadowDetailScale", shadowDetailScale);

			shadowCompute.Dispatch(kernel, Mathf.CeilToInt(width / 8f), Mathf.CeilToInt(height / 8f), 1);

			// Through CloudShadowGlobals rather than Shader.SetGlobal directly, because the
			// atmosphere's three COMPUTE consumers cannot read shader globals - a kernel's textures
			// have to be bound per kernel. One writer, two representations, so they cannot drift.
			//
			// The deck radius is published beside the SHADOW map, not only beside the radiance map:
			// the raymarch needs it to find where a sun ray crosses the deck, and shafts must work in
			// profiles where the radiance map is off.
			CloudShadowGlobals.Publish(shadowMap, shadowStrength, shaftStrength,
				(InnerRadius + OuterRadius) * 0.5f);
			publishedShadows = true;
		}

		void ResolveSun()
		{
			if (sunLight != null) { return; }
			GameObject sunObject = GameObject.FindGameObjectWithTag("Sun");
			sunLight = sunObject != null ? sunObject.GetComponent<Light>() : null;
		}

		/// <summary>
		/// One genus, as weather-field values.
		///
		/// This table is a thesis artefact as much as a debug aid: RQ3 grades authoring flexibility on
		/// "how predictably does a parameter map to a visible outcome", and the mapping from a genus
		/// name to these six numbers is exactly that evidence.
		///
		/// baseOffset is a fraction of the shell. At 0.91 km per world unit and a shell running 0.91
		/// to 9.1 km, the taxonomy's bands land at: low below 0.13, mid 0.13 to 0.62, high above
		/// 0.62. The high genera turn the low layer off and vice versa - a genus image should show
		/// one genus.
		/// </summary>
		void GenusSettings(Genus genus, out Vector4 low, out Vector2 high, out float baseOffset)
		{
			// low = coverage, precipitation, form, unused
			switch (genus)
			{
				// --- low band, floor on the deck -----------------------------------------------
				case Genus.Stratus:
					low = new Vector4(0.95f, 0f, 0.00f, 0); high = Vector2.zero; baseOffset = 0.00f; return;
				case Genus.Stratocumulus:
					low = new Vector4(0.80f, 0f, 0.28f, 0); high = Vector2.zero; baseOffset = 0.02f; return;
				case Genus.Cumulus:
					low = new Vector4(0.50f, 0f, 0.50f, 0); high = Vector2.zero; baseOffset = 0.03f; return;
				case Genus.Cumulonimbus:
					// Precipitation is what makes it tower and darken - the -nimbus affix, not a
					// separate profile.
					low = new Vector4(0.80f, 0.90f, 1.00f, 0); high = Vector2.zero; baseOffset = 0.00f; return;

				// --- mid band: the same forms, floor raised ------------------------------------
				case Genus.Altostratus:
					low = new Vector4(0.95f, 0f, 0.00f, 0); high = Vector2.zero; baseOffset = 0.32f; return;
				case Genus.Altocumulus:
					low = new Vector4(0.70f, 0f, 0.32f, 0); high = Vector2.zero; baseOffset = 0.30f; return;
				case Genus.Nimbostratus:
					// Thick, featureless and raining. Its floor starts mid and precipitation drags it
					// down, which is why it reads as a low grey overcast despite being a mid genus.
					low = new Vector4(1.00f, 0.75f, 0.10f, 0); high = Vector2.zero; baseOffset = 0.22f; return;

				// --- high band: the ice layer, low layer off ------------------------------------
				case Genus.Cirrus:
					low = Vector4.zero; high = new Vector2(0.35f, 0.00f); baseOffset = 0f; return;
				case Genus.Cirrocumulus:
					low = Vector4.zero; high = new Vector2(0.55f, 0.50f); baseOffset = 0f; return;
				case Genus.Cirrostratus:
					low = Vector4.zero; high = new Vector2(0.70f, 1.00f); baseOffset = 0f; return;

				default:
					low = Vector4.zero; high = Vector2.zero; baseOffset = 0f; return;
			}
		}

		/// <summary>Binds the override to whichever consumer. Off leaves every field at its map value.</summary>
		void BindGenusOverride(System.Action<string, float> setFloat, System.Action<string, Vector4> setVector)
		{
			GenusSettings(genusOverride, out Vector4 low, out Vector2 high, out float baseOffset);

			setFloat("cloudWeatherOverrideOn", genusOverride == Genus.Off ? 0f : 1f);
			setVector("cloudWeatherOverride", low);
			setVector("cloudHighWeatherOverride", new Vector4(high.x, high.y, 0, 0));
			setFloat("cloudGenusBaseOffset", baseOffset);
		}

		void SetProperties()
		{
			material.SetTexture("CloudShapeNoise", shapeNoise);
			material.SetTexture("CloudDetailNoise", detailNoise);
			material.SetTexture("CloudWeatherMapTex", weatherMap);
			material.SetTexture("CloudWeatherMapHighTex", weatherMapHigh);

			material.SetFloat("cloudInnerRadius", InnerRadius);
			material.SetFloat("cloudOuterRadius", OuterRadius);

			material.SetFloat("cloudShapeScale", shapeScale);
			material.SetFloat("cloudDetailScale", detailScale);
			material.SetFloat("cloudDetailWeight", detailWeight);
			material.SetFloat("cloudDensityMultiplier", densityMultiplier);
			material.SetFloat("cloudCoverageMultiplier", coverageMultiplier);
			material.SetFloat("cloudTypeBias", typeOffset);
			material.SetFloat("cloudShapeResolution", shapeNoise != null ? shapeNoise.width : 128);
			material.SetFloat("cloudDetailResolution", detailNoise != null ? detailNoise.width : 32);
			material.SetFloat("cloudBaseVariation", baseVariation);
			material.SetFloat("cloudBandThickness", bandThickness);
			material.SetFloat("cloudEdgeSoftness", edgeSoftness);
			material.SetFloat("cloudPrecipDensity", precipDensity);
			material.SetFloat("cloudPrecipTypePush", precipTypePush);
			material.SetFloat("cloudPrecipBaseDrop", precipBaseDrop);

			// The volumes drift faster than the weather field: the weather map is the slow-moving
			// system, the noise is the air moving through it.
			material.SetVector("cloudShapeWind", ShapeWind);
			material.SetVector("cloudDetailWind", DetailWind);

			material.SetFloat("cloudHighStrength", highStrength);
			material.SetFloat("cloudHighBase", highBase);
			material.SetFloat("cloudHighThickness", highThickness);
			material.SetFloat("cloudHighScale", highScale);
			material.SetFloat("cloudHighDensityMultiplier", highDensityMultiplier);
			material.SetFloat("cloudHighCoverageMultiplier", highCoverageMultiplier);
			material.SetFloat("cloudHighStretch", highStretch);
			material.SetFloat("cloudHighGranularity", highGranularity);
			material.SetVector("cloudHighWindDir", HighWindDir);
			material.SetVector("cloudHighWind", HighWind);
			BindGenusOverride(material.SetFloat, material.SetVector);

			material.SetFloat("cloudStepSize", stepSize);
			// CameraRegion leaves the clouds rendering and shows the region as a corner swatch, so
			// the two can be watched together; the others replace the image.
			material.SetInt("cloudDebugMode", debugMode == DebugMode.CameraRegion ? 0 : (int)debugMode);

			float region = -1f;
			if (debugMode == DebugMode.CameraRegion && cam != null)
			{
				float camRadius = cam.transform.position.magnitude;
				region = camRadius > OuterRadius ? 0f : (camRadius > InnerRadius ? 1f : 2f);
			}
			material.SetFloat("_CloudDebugRegion", region);
			material.SetInt("cloudMaxSteps", maxSteps);
			material.SetFloat("cloudJitterStrength", jitterStrength);
			material.SetFloat("cloudExtinction", extinction);

			ResolveSun();
			// -forward, matching how every other consumer in the project derives the sun direction.
			Vector3 sunDir = sunLight != null ? -sunLight.transform.forward : Vector3.up;
			Color sunColour = sunLight != null ? sunLight.color : Color.white;

			material.SetVector("cloudSunDir", sunDir);
			// The gradient is Sun.cs's camera-keyed approximation, which the land and the ocean both
			// stopped using once transmittance could answer the same question per pixel. Gated the
			// same way, so it survives only as the baseline path where there is no LUT to ask.
			float gate = Shader.GetGlobalFloat("skyReflectionStrength");
			Color gradient = Color.Lerp(sunColour, Color.white, gate);
			material.SetVector("cloudSunColour", new Vector4(gradient.r, gradient.g, gradient.b, 1));
			material.SetFloat("cloudSunIntensity", sunIntensity);
			material.SetFloat("cloudSunTransmittanceWeight", sunTransmittanceWeight);
			material.SetFloat("cloudAmbientIntensity", ambientIntensity);
			material.SetFloat("cloudAmbientHorizon", ambientHorizon);
			material.SetVector("cloudAmbientFallback", new Vector4(ambientFallback.r, ambientFallback.g, ambientFallback.b, 1));
			material.SetVector("cloudNightAmbient", new Vector4(nightAmbient.r, nightAmbient.g, nightAmbient.b, 1));

			material.SetFloat("cloudAerialStrength", aerialStrength);

			material.SetFloat("cloudLightMarchLength", lightMarchLength);
			material.SetFloat("cloudLightAbsorption", lightAbsorption);
			material.SetFloat("cloudConeSpread", coneSpread);
			material.SetFloat("cloudLightMarchDetail", lightMarchDetail);
			material.SetFloat("cloudPowderStrength", powderStrength);

			material.SetFloat("cloudPhaseForward", phaseForward);
			material.SetFloat("cloudPhaseBackward", phaseBackward);
			material.SetFloat("cloudPhaseBlend", phaseBlend);
			material.SetFloat("cloudSilverIntensity", silverIntensity);
			material.SetFloat("cloudSilverSpread", silverSpread);
			material.SetFloat("cloudMoonIntensity", moonIntensity);
			material.SetFloat("cloudMoonSilverIntensity", moonSilverIntensity);
		}
	}
}
