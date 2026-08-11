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
	/// Sits AFTER the atmosphere composite in the chain, which is why the clouds will apply their
	/// own aerial perspective in stage 4 rather than inheriting it.
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

		[Tooltip("Cloud type varies on its own, much larger scale - a region can be solidly covered " +
			"in flat stratus or lightly dotted with cumulus, and tying type to coverage would " +
			"collapse the genera axis RQ1 scores.")]
		[Range(0.1f, 6f)] public float typeScale = 1.1f;
		[Range(-0.5f, 0.5f)] public float typeBias = 0f;

		[Range(0.2f, 12f)] public float precipitationScale = 3.5f;
		[Range(-0.5f, 0.5f)] public float precipitationBias = -0.1f;

		[Tooltip("Speed the weather field drifts, in the sphere's own space rather than as a UV " +
			"scroll - a scroll would slide features along latitude lines and tear them at the poles.")]
		public float weatherWindSpeed = 0.004f;

		[Header("Shell")]
		[Tooltip("Sea level, in world units. Its own field rather than the atmosphere's planetRadius " +
			"global, which is zero whenever the atmosphere is off - and the clouds have to keep " +
			"working in the baseline and ablation profiles.")]
		public float bodyRadius = 150f;

		[Tooltip("Cloud base above sea level, world units. At the scene's scale one unit is about " +
			"0.91 km, so the low cloud band that THESIS.md makes RQ1's rubric - below 2000 m - is " +
			"under 2.2 units.")]
		public float cloudBottomAltitude = 0.55f;
		public float cloudTopAltitude = 2.2f;

		[Header("Density")]
		[Range(0.001f, 0.5f)] public float shapeScale = 0.06f;
		[Range(0.01f, 4f)] public float detailScale = 0.5f;
		[Range(0f, 1f)] public float detailWeight = 0.35f;
		[Range(0f, 8f)] public float densityMultiplier = 1.5f;
		[Range(0f, 3f)] public float coverageMultiplier = 1f;
		[Range(-0.5f, 0.5f)] public float typeOffset = 0f;
		public Vector3 shapeWindDirection = new Vector3(1f, 0.1f, 0.2f);
		public float shapeWindSpeed = 0.02f;
		public float detailWindSpeed = 0.05f;

		[Header("March")]
		[Tooltip("Target world-unit length of one step. The step COUNT follows from the segment, so " +
			"a vertical ray through the shell and a grazing ray through a long chord are both " +
			"sampled properly. The reference project instead hardcodes a step of 11 world units, " +
			"which here would step over the whole layer six times over in one step.")]
		[Range(0.005f, 1f)] public float stepSize = 0.05f;
		[Range(8, 256)] public int minSteps = 24;
		[Range(16, 512)] public int maxSteps = 128;
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

		[Header("Light march")]
		[Tooltip("How far toward the sun the light march reaches, in world units. Around one shell " +
			"thickness is usually right - beyond that it is sampling air.")]
		[Range(0.1f, 8f)] public float lightMarchLength = 1.6f;
		[Range(0.1f, 8f)] public float lightAbsorption = 2.5f;
		[Tooltip("How far the light march spreads into a cone. A straight line makes every cloud " +
			"self-shadow like a slab; the cone is what lets light wrap around a billow.")]
		[Range(0f, 1f)] public float coneSpread = 0.25f;
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

		[Header("Shadows on the ground")]
		public ComputeShader shadowCompute;
		[Tooltip("Equirectangular, so it needs no shadow frustum and does not change with the " +
			"camera. Modest resolution is enough: a shadow cast from a kilometre up carries no " +
			"high-frequency detail anyway.")]
		public Vector2Int shadowMapSize = new Vector2Int(512, 256);
		[Range(2, 32)] public int shadowSteps = 10;
		[Range(0.1f, 8f)] public float shadowAbsorption = 1.2f;
		[Tooltip("0 disables cloud shadows entirely, which is also what happens when this effect is " +
			"disabled - so a clouds-off benchmark profile gets an unshadowed ground for free.")]
		[Range(0f, 1f)] public float shadowStrength = 0.85f;

		RenderTexture weatherMap;
		RenderTexture shadowMap;

		// Elapsed time, accumulated rather than read from Time.time, so the benchmark's fixed
		// captureDeltaTime advances the weather at the same rate a real frame would.
		float elapsed;

		// Resolved by tag, the same way AtmosphereEffect finds it - this is a ScriptableObject, so
		// it cannot hold a scene reference.
		Light sunLight;

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
				Shader.SetGlobalFloat("cloudShadowStrength", 0f);
				return;
			}

			RenderWeatherMap();
			RenderShadowMap();
		}

		void ReleaseWeatherMap()
		{
			if (weatherMap != null)
			{
				weatherMap.Release();
				DestroyImmediate(weatherMap);
				weatherMap = null;
			}
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

			// Two passes: march into an offscreen target, then composite it over the frame. The
			// split is what allows the march to run at a lower resolution than the frame - the
			// whole of the Half mode - and it keeps the two modes on one code path, which is what
			// makes the cost difference between them attributable to the resolution alone.
			int divisor = costMode == CostMode.Full ? 1 : 2;
			int width = Mathf.Max(1, source.width / divisor);
			int height = Mathf.Max(1, source.height / divisor);

			// Half format: the cloud's accumulated luminance is not bounded by 1, so an 8-bit target
			// would clip the lit tops flat.
			RenderTexture cloudTex = RenderTexture.GetTemporary(
				width, height, 0, RenderTextureFormat.ARGBHalf);
			cloudTex.filterMode = FilterMode.Bilinear;

			Graphics.Blit(source, cloudTex, material, 0);

			material.SetTexture("_CloudTex", cloudTex);
			material.SetVector("_CloudTexSize", new Vector4(width, height, 1f / width, 1f / height));
			material.SetFloat("_CloudUpsample", divisor);
			material.SetFloat("_CloudDepthRejection", depthRejection);

			Graphics.Blit(source, target, material, 1);

			RenderTexture.ReleaseTemporary(cloudTex);
		}

		void RenderWeatherMap()
		{
			int width = Mathf.Max(8, weatherMapSize.x);
			int height = Mathf.Max(8, weatherMapSize.y);

			if (weatherMap == null || !weatherMap.IsCreated() ||
				weatherMap.width != width || weatherMap.height != height)
			{
				ReleaseWeatherMap();
				weatherMap = new RenderTexture(width, height, 0, GraphicsFormat.R8G8B8A8_UNorm)
				{
					enableRandomWrite = true,
					// Longitude wraps, latitude does not - the same split the city light field uses.
					wrapModeU = TextureWrapMode.Repeat,
					wrapModeV = TextureWrapMode.Clamp,
					filterMode = FilterMode.Bilinear,
					name = "Cloud Weather Map"
				};
				weatherMap.Create();
			}

			elapsed += Time.deltaTime;
			float weatherOffset = elapsed * weatherWindSpeed;

			int kernel = weatherCompute.FindKernel("CSWeatherMap");
			weatherCompute.SetTexture(kernel, "Result", weatherMap);
			weatherCompute.SetVector("weatherMapSize", new Vector2(width, height));
			weatherCompute.SetInt("octaves", weatherOctaves);
			weatherCompute.SetFloat("lacunarity", weatherLacunarity);
			weatherCompute.SetFloat("gain", weatherGain);
			weatherCompute.SetFloat("coverageScale", coverageScale);
			weatherCompute.SetFloat("coverageBias", coverageBias);
			weatherCompute.SetFloat("coverageContrast", coverageContrast);
			weatherCompute.SetFloat("typeScale", typeScale);
			weatherCompute.SetFloat("typeBias", typeBias);
			weatherCompute.SetFloat("precipitationScale", precipitationScale);
			weatherCompute.SetFloat("precipitationBias", precipitationBias);
			weatherCompute.SetVector("windOffset", new Vector4(weatherOffset, weatherOffset * 0.3f, weatherOffset * 0.7f, 0));

			weatherCompute.Dispatch(kernel, Mathf.CeilToInt(width / 8f), Mathf.CeilToInt(height / 8f), 1);
		}

		float InnerRadius => bodyRadius + cloudBottomAltitude;
		float OuterRadius => bodyRadius + Mathf.Max(cloudBottomAltitude + 0.01f, cloudTopAltitude);

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
		void BindDensity(ComputeShader compute, int kernel)
		{
			compute.SetTexture(kernel, "CloudShapeNoise", shapeNoise);
			compute.SetTexture(kernel, "CloudDetailNoise", detailNoise);
			compute.SetTexture(kernel, "CloudWeatherMapTex", weatherMap);

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
			compute.SetVector("cloudShapeWind", ShapeWind);
			compute.SetVector("cloudDetailWind", DetailWind);
		}

		void RenderShadowMap()
		{
			if (shadowCompute == null || shadowStrength <= 0f)
			{
				Shader.SetGlobalFloat("cloudShadowStrength", 0f);
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

			shadowCompute.Dispatch(kernel, Mathf.CeilToInt(width / 8f), Mathf.CeilToInt(height / 8f), 1);

			Shader.SetGlobalTexture("CloudShadowMap", shadowMap);
			Shader.SetGlobalFloat("cloudShadowStrength", shadowStrength);
		}

		void ResolveSun()
		{
			if (sunLight != null) { return; }
			GameObject sunObject = GameObject.FindGameObjectWithTag("Sun");
			sunLight = sunObject != null ? sunObject.GetComponent<Light>() : null;
		}

		void SetProperties()
		{
			material.SetTexture("CloudShapeNoise", shapeNoise);
			material.SetTexture("CloudDetailNoise", detailNoise);
			material.SetTexture("CloudWeatherMapTex", weatherMap);

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

			// The volumes drift faster than the weather field: the weather map is the slow-moving
			// system, the noise is the air moving through it.
			material.SetVector("cloudShapeWind", ShapeWind);
			material.SetVector("cloudDetailWind", DetailWind);

			material.SetFloat("cloudStepSize", stepSize);
			material.SetInt("cloudMinSteps", minSteps);
			material.SetInt("cloudMaxSteps", Mathf.Max(minSteps, maxSteps));
			material.SetFloat("cloudJitterStrength", jitterStrength);
			material.SetFloat("cloudExtinction", extinction);

			ResolveSun();
			// -forward, matching how every other consumer in the project derives the sun direction.
			Vector3 sunDir = sunLight != null ? -sunLight.transform.forward : Vector3.up;
			Color sunColour = sunLight != null ? sunLight.color : Color.white;

			material.SetVector("cloudSunDir", sunDir);
			material.SetVector("cloudSunColour", new Vector4(sunColour.r, sunColour.g, sunColour.b, 1));
			material.SetFloat("cloudSunIntensity", sunIntensity);
			material.SetFloat("cloudSunTransmittanceWeight", sunTransmittanceWeight);
			material.SetFloat("cloudAmbientIntensity", ambientIntensity);
			material.SetFloat("cloudAmbientHorizon", ambientHorizon);
			material.SetVector("cloudAmbientFallback", new Vector4(ambientFallback.r, ambientFallback.g, ambientFallback.b, 1));

			material.SetFloat("cloudLightMarchLength", lightMarchLength);
			material.SetFloat("cloudLightAbsorption", lightAbsorption);
			material.SetFloat("cloudConeSpread", coneSpread);
			material.SetFloat("cloudPowderStrength", powderStrength);

			material.SetFloat("cloudPhaseForward", phaseForward);
			material.SetFloat("cloudPhaseBackward", phaseBackward);
			material.SetFloat("cloudPhaseBlend", phaseBlend);
		}
	}
}
