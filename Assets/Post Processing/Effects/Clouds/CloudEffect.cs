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
		[Range(0f, 8f)] public float sunIntensity = 1.4f;

		[Tooltip("Scales skylight on the clouds, from the same sky-view LUT the ocean and land use. " +
			"At low sun this does more of the work than the direct term - it is what puts the " +
			"sunset on the undersides.")]
		[Range(0f, 4f)] public float ambientIntensity = 1f;

		[Tooltip("Stands in for skylight when there is no physically based atmosphere bound, so the " +
			"clouds are still lit in the baseline and ablation profiles rather than going black.")]
		public Color ambientFallback = new Color(0.4f, 0.45f, 0.55f);

		[Header("Light march")]
		[Tooltip("How far toward the sun the light march reaches, in world units. Around one shell " +
			"thickness is usually right - beyond that it is sampling air.")]
		[Range(0.1f, 8f)] public float lightMarchLength = 1.6f;
		[Range(0.1f, 8f)] public float lightAbsorption = 1.1f;
		[Tooltip("How far the light march spreads into a cone. A straight line makes every cloud " +
			"self-shadow like a slab; the cone is what lets light wrap around a billow.")]
		[Range(0f, 1f)] public float coneSpread = 0.25f;
		[Tooltip("Beer-Powder. Plain Beer makes cloud edges read as cut-outs; this restores the dark " +
			"edge that comes from light having to scatter into a thin volume before it can leave. " +
			"The reference project omits it entirely.")]
		[Range(0f, 1f)] public float powderStrength = 0.7f;

		[Header("Phase")]
		[Range(0f, 0.99f)] public float phaseForward = 0.8f;
		[Range(0f, 0.99f)] public float phaseBackward = 0.3f;
		[Range(0f, 1f)] public float phaseBlend = 0.5f;

		RenderTexture weatherMap;

		// Elapsed time, accumulated rather than read from Time.time, so the benchmark's fixed
		// captureDeltaTime advances the weather at the same rate a real frame would.
		float elapsed;

		// Resolved by tag, the same way AtmosphereEffect finds it - this is a ScriptableObject, so
		// it cannot hold a scene reference.
		Light sunLight;

		public RenderTexture WeatherMap => weatherMap;

		public override void OnDestroy()
		{
			ReleaseWeatherMap();
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

			RenderWeatherMap();
			SetProperties();
			Graphics.Blit(source, target, material);
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

			material.SetFloat("cloudInnerRadius", bodyRadius + cloudBottomAltitude);
			material.SetFloat("cloudOuterRadius", bodyRadius + Mathf.Max(cloudBottomAltitude + 0.01f, cloudTopAltitude));

			material.SetFloat("cloudShapeScale", shapeScale);
			material.SetFloat("cloudDetailScale", detailScale);
			material.SetFloat("cloudDetailWeight", detailWeight);
			material.SetFloat("cloudDensityMultiplier", densityMultiplier);
			material.SetFloat("cloudCoverageMultiplier", coverageMultiplier);
			material.SetFloat("cloudTypeBias", typeOffset);

			Vector3 wind = shapeWindDirection.sqrMagnitude > 1e-6f ? shapeWindDirection.normalized : Vector3.right;
			// The volumes drift faster than the weather field: the weather map is the slow-moving
			// system, the noise is the air moving through it.
			material.SetVector("cloudShapeWind", wind * (elapsed * shapeWindSpeed));
			material.SetVector("cloudDetailWind", wind * (elapsed * detailWindSpeed));

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
			material.SetFloat("cloudAmbientIntensity", ambientIntensity);
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
