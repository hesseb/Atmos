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
[CreateAssetMenu(menuName = "Testbed/Renderer Profile", fileName = "Renderer Profile")]
public class PostProcessRendererProfile : RendererProfile
{
	[System.Serializable]
	public struct EffectToggle
	{
		public PostProcessingEffect effect;
		public bool enabled;
	}

	[System.Serializable]
	public struct ObjectToggle
	{
		// Scene path, e.g. "Game/World/Baseline Sky". A ScriptableObject asset cannot hold
		// a reference to a scene object, so these are resolved by name at run time.
		public string scenePath;
		public bool active;
	}

	/// <summary>Which sky this profile wants. Orthogonal to the effect toggles, because the
	/// sky is drawn from a command buffer rather than from the post-processing chain.</summary>
	public enum SkyOverride
	{
		/// <summary>Inherit whatever the scene is set to.</summary>
		LeaveAlone,
		/// <summary>Baseline renderer off. The physically based sky draws if its effect is
		/// enabled; otherwise nothing draws a sky at all.</summary>
		Off,
		/// <summary>Hand-authored gradient LUT.</summary>
		Gradient,
		/// <summary>Gradient LUT baked off the physically based renderer. Same cost as
		/// Gradient; the difference is the authoring method.</summary>
		GradientBaked,
		Cubemap,
		/// <summary>The measurement control: the sky pass with no shading.</summary>
		Null
	}

	[Tooltip("Effects to force on or off. Effects not listed keep whatever the scene has.")]
	public EffectToggle[] effects;

	[Tooltip("Scene objects to activate or deactivate, by hierarchy path.")]
	public ObjectToggle[] objects;

	[Tooltip("Which sky to draw. Every profile should state this explicitly - LeaveAlone " +
		"inherits whatever the previous pass happened to leave, which is how state leaks " +
		"between passes.")]
	public SkyOverride sky = SkyOverride.LeaveAlone;

	public override void Apply(BenchmarkSceneRefs refs, RestoreScope scope)
	{
		if (effects != null)
		{
			foreach (EffectToggle toggle in effects)
			{
				if (toggle.effect == null) { continue; }

				PostProcessingEffect effect = toggle.effect;
				scope.Set(() => effect.enabled, v => effect.enabled = v, toggle.enabled);
			}
		}

		if (objects != null)
		{
			foreach (ObjectToggle toggle in objects)
			{
				if (string.IsNullOrEmpty(toggle.scenePath)) { continue; }

				GameObject go = GameObject.Find(toggle.scenePath);
				if (go == null)
				{
					Debug.LogWarning($"[Benchmark] profile '{id}': no scene object at " +
						$"'{toggle.scenePath}' - skipping. Note GameObject.Find cannot see " +
						"inactive objects, so an object that starts disabled must be " +
						"activated by hand or referenced another way.", this);
					continue;
				}

				GameObject captured = go;
				scope.Set(() => captured.activeSelf, v => captured.SetActive(v), toggle.active);
			}
		}

		ApplySkyOverride(refs, scope);
	}

	void ApplySkyOverride(BenchmarkSceneRefs refs, RestoreScope scope)
	{
		if (sky == SkyOverride.LeaveAlone) { return; }

		BaselineSkyRenderer baseline = refs.baselineSky;
		if (baseline == null)
		{
			Debug.LogWarning($"[Benchmark] profile '{id}' asks for sky '{sky}', but there is no " +
				"BaselineSkyRenderer in the scene - the sky will be whatever the scene has.", this);
			return;
		}

		// Variant first, so that enabling the renderer never briefly attaches the wrong sky.
		if (sky != SkyOverride.Off)
		{
			BaselineSkyRenderer.Variant variant;
			switch (sky)
			{
				case SkyOverride.Cubemap: variant = BaselineSkyRenderer.Variant.Cubemap; break;
				case SkyOverride.GradientBaked: variant = BaselineSkyRenderer.Variant.GradientBaked; break;
				case SkyOverride.Null: variant = BaselineSkyRenderer.Variant.Null; break;
				default: variant = BaselineSkyRenderer.Variant.Gradient; break;
			}

			scope.Set(() => baseline.variant, v => baseline.variant = v, variant);
		}

		// A component, so Unity discards this on exiting play mode even if the scope somehow
		// does not run - unlike the effect flags above, which are asset state.
		scope.Set(() => baseline.enabled, v => baseline.enabled = v, sky != SkyOverride.Off);
	}

	public override string DescribeSettings(BenchmarkSceneRefs refs)
	{
		var sb = new StringBuilder();
		sb.Append("effects: ");

		if (refs.postProcessing != null && refs.postProcessing.effects != null)
		{
			bool first = true;
			foreach (PostProcessingEffect effect in refs.postProcessing.effects)
			{
				if (effect == null) { continue; }
				if (!first) { sb.Append(", "); }
				sb.Append(effect.name).Append('=').Append(effect.enabled ? "on" : "off");
				first = false;
			}
		}

		// Which sky ACTUALLY rendered, read back from the renderer rather than from what this
		// profile requested - the same principle as hashing the observed camera pose rather
		// than the planned one. A profile that silently failed to apply must not be able to
		// claim in run.json that it did.
		if (refs.renderingManager != null)
		{
			sb.Append(" | sky: ").Append(refs.renderingManager.ActiveMode);

			if (refs.baselineSky != null && refs.renderingManager.ActiveMode == SkyMode.Baseline)
			{
				sb.Append('/').Append(refs.baselineSky.variant);
			}
		}

		// The atmosphere's cost-relevant parameters. Whether the sky raymarch runs 64 or
		// 256 steps changes the result by more than most things being compared, so the
		// numbers are meaningless without it recorded.
		AtmosphereEffect atmosphere = FindAtmosphere(refs);
		if (atmosphere != null)
		{
			sb.Append(" | atmosphere: enabled=").Append(atmosphere.enabled)
			  .Append(", skySteps=").Append(atmosphere.numSkyScatteringSteps)
			  .Append(", skySize=").Append(atmosphere.skyRenderSize.x).Append('x')
			  .Append(atmosphere.skyRenderSize.y)
			  .Append(", aerialStepsPerSlice=").Append(atmosphere.aerialStepsPerSlice)
			  .Append(", aerialLUT=").Append(atmosphere.aerialPerspectiveLUTSize)
			  .Append(", transmittanceLUT=").Append(atmosphere.transmittanceLUTSize.x).Append('x')
			  .Append(atmosphere.transmittanceLUTSize.y);
		}

		return sb.ToString();
	}

	static AtmosphereEffect FindAtmosphere(BenchmarkSceneRefs refs)
	{
		if (refs.postProcessing == null || refs.postProcessing.effects == null) { return null; }

		foreach (PostProcessingEffect effect in refs.postProcessing.effects)
		{
			if (effect is AtmosphereEffect atmosphere) { return atmosphere; }
		}
		return null;
	}
}
