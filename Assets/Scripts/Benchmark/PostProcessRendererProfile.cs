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

	[Tooltip("Effects to force on or off. Effects not listed keep whatever the scene has.")]
	public EffectToggle[] effects;

	[Tooltip("Scene objects to activate or deactivate, by hierarchy path.")]
	public ObjectToggle[] objects;

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
			  .Append(", aerialSteps=").Append(atmosphere.numAerialScatteringSteps)
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
