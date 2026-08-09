using UnityEditor;
using UnityEngine;

/// <summary>
/// Authors the full set of renderer profiles.
///
/// Generated in code rather than hand-authored so the set stays internally consistent - the
/// important property is that **every profile states every switch explicitly**. A profile
/// that omits a toggle inherits whatever the previous pass left behind, which is how one
/// pass's configuration silently contaminates the next.
///
/// The set is not just "physically based versus cheap". It is arranged so the cost of the
/// sky pass can be decomposed rather than merely totalled:
///
///   nullsky - noatmo        the pass structure alone: two full-screen blits and a temp RT
///   baseline - nullsky      the cheap shading model
///   pbr - nullsky           raymarched scattering + aerial perspective + LUT computes
///   pbr - baseline          the headline RQ2 number
///
/// and so the two authoring methods for the same runtime cost can be separated:
///
///   baseline-gradient vs baseline-baked   identical shader path and cost; any visual
///                                         difference is purely down to how the LUT was made
/// </summary>
static class RendererProfiles
{
	const string Folder = "Assets/Data/RenderingProfiles";

	[MenuItem("Testbed/Benchmark/Create Renderer Profiles")]
	static void Create()
	{
		System.IO.Directory.CreateDirectory(Folder);

		AtmosphereEffect atmosphere = FindEffect<AtmosphereEffect>();
		AerialPerspectiveSimple aerial = FindEffect<AerialPerspectiveSimple>();

		if (atmosphere == null)
		{
			Debug.LogError("[Benchmark] no AtmosphereEffect asset found.");
			return;
		}
		if (aerial == null)
		{
			Debug.LogWarning("[Benchmark] no AerialPerspectiveSimple asset found - the baseline " +
				"profiles will not enable cheap aerial perspective, which makes them one " +
				"feature short of the physically based path.");
		}

		Save(Build("pbr", "Physically based: raymarched sky plus scattering-LUT aerial perspective.",
			atmosphere, atmosphereOn: true, aerial, aerialOn: false,
			PostProcessRendererProfile.SkyOverride.Off));

		Save(Build("baseline-gradient", "Cheap sky from a hand-authored gradient LUT, plus " +
			"exponential distance fog. The baseline the report's RQ2 is phrased against.",
			atmosphere, atmosphereOn: false, aerial, aerialOn: true,
			PostProcessRendererProfile.SkyOverride.Gradient));

		Save(Build("baseline-baked", "Same shader and same cost as baseline-gradient, but the " +
			"LUT is baked off the physically based renderer. Isolates authoring from cost.",
			atmosphere, atmosphereOn: false, aerial, aerialOn: true,
			PostProcessRendererProfile.SkyOverride.GradientBaked));

		Save(Build("baseline-cubemap", "Cheapest sky: a single static cubemap that cannot " +
			"respond to the sun moving. The literal reading of 'textured skybox'.",
			atmosphere, atmosphereOn: false, aerial, aerialOn: true,
			PostProcessRendererProfile.SkyOverride.Cubemap));

		Save(Build("nullsky", "Control, not a renderer: the sky pass with a passthrough " +
			"fragment. Subtract from any sky to remove the cost of the pass structure.",
			atmosphere, atmosphereOn: false, aerial, aerialOn: false,
			PostProcessRendererProfile.SkyOverride.Null));

		Save(Build("noatmo", "Control: no sky pass at all, and no aerial perspective. This is " +
			"an ablation rather than a baseline - it renders a black sky.",
			atmosphere, atmosphereOn: false, aerial, aerialOn: false,
			PostProcessRendererProfile.SkyOverride.Off));

		AssetDatabase.SaveAssets();
		AssetDatabase.Refresh();
		Debug.Log($"[Benchmark] wrote 6 renderer profiles to {Folder}\n" +
			"  Assign them to the BenchmarkRunner's Profiles array in the order you want the " +
			"summary's baseline column to be - the first entry is what deltas are measured from.");
	}

	static PostProcessRendererProfile Build(string id, string description,
		AtmosphereEffect atmosphere, bool atmosphereOn,
		AerialPerspectiveSimple aerial, bool aerialOn,
		PostProcessRendererProfile.SkyOverride sky)
	{
		var profile = ScriptableObject.CreateInstance<PostProcessRendererProfile>();
		profile.id = id;
		profile.description = description;
		profile.sky = sky;
		profile.objects = new PostProcessRendererProfile.ObjectToggle[0];

		// Both effects are listed on every profile, including where the value matches the
		// scene default. Leaving one out would let a previous pass's value survive.
		var effects = new System.Collections.Generic.List<PostProcessRendererProfile.EffectToggle>
		{
			new PostProcessRendererProfile.EffectToggle { effect = atmosphere, enabled = atmosphereOn }
		};

		if (aerial != null)
		{
			effects.Add(new PostProcessRendererProfile.EffectToggle { effect = aerial, enabled = aerialOn });
		}

		profile.effects = effects.ToArray();
		return profile;
	}

	static void Save(PostProcessRendererProfile profile)
	{
		string path = $"{Folder}/{profile.id}.asset";

		// Overwrite in place: CreateAsset mints a new GUID, which would null out every
		// reference to the profile - most obviously the BenchmarkRunner's Profiles array -
		// each time this menu item is used. Regenerating the set is meant to be routine.
		var existing = AssetDatabase.LoadAssetAtPath<PostProcessRendererProfile>(path);
		if (existing != null)
		{
			// CopySerialized copies m_Name too, and a fresh instance has none.
			profile.name = existing.name;
			EditorUtility.CopySerialized(profile, existing);
			EditorUtility.SetDirty(existing);
			Object.DestroyImmediate(profile);
			return;
		}

		profile.name = profile.id;
		AssetDatabase.CreateAsset(profile, path);
	}

	static T FindEffect<T>() where T : PostProcessingEffect
	{
		foreach (string guid in AssetDatabase.FindAssets($"t:{typeof(T).Name}"))
		{
			var effect = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid));
			if (effect != null) { return effect; }
		}
		return null;
	}
}
