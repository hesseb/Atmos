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
///   nullsky - noatmo               the sky pass structure: two full-screen blits, temp RT
///   nullsky-aerial - nullsky       the cheap aerial perspective pass
///   baseline - nullsky-aerial      the cheap sky shading, and nothing else
///   pbr - baseline                 the headline RQ2 number
///
/// The `nullsky-aerial` arm is not redundant. Subtracting plain `nullsky` from a baseline
/// bundles the entire aerial perspective pass in with the sky shading and reports the sum as
/// if it were the shading cost - measured at 0.162 ms when the shading alone is a fraction
/// of that.
///
/// and so the two authoring methods for the same runtime cost can be separated:
///
///   baseline-gradient vs baseline-baked   identical shader path and cost; any visual
///                                         difference is purely down to how the LUT was made
///
/// The cloud series is arranged the same way and for the same reasons:
///
///   clouds-baseline-null      - pbr                       the cloud pass structure alone
///   clouds-baseline-authored  - clouds-baseline-null      the cheap shading model
///   clouds-full               - clouds-baseline-null      the march
///   clouds-baseline-mesh      - clouds-baseline-authored  what the delivery mechanism is worth
///
/// and the two authoring methods are separated from the runtime cost, as with the sky:
///
///   clouds-baseline-authored vs -baked    identical shader path and cost; only the content
///                                         differs, which separates a weaker technique from
///                                         weaker art
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
		Clouds.CloudEffect clouds = FindEffect<Clouds.CloudEffect>();

		if (clouds == null)
		{
			Debug.LogWarning("[Benchmark] no CloudEffect asset found - the cloud profiles will be " +
				"skipped, and the sky profiles will not state a cloud toggle, so whatever the " +
				"previous pass left enabled will survive into them.");
		}

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
			clouds, PostProcessRendererProfile.SkyOverride.Off));

		Save(Build("baseline-gradient", "Cheap sky from a hand-authored gradient LUT, plus " +
			"exponential distance fog. The baseline the report's RQ2 is phrased against.",
			atmosphere, atmosphereOn: false, aerial, aerialOn: true,
			clouds, PostProcessRendererProfile.SkyOverride.Gradient));

		Save(Build("baseline-baked", "Same shader and same cost as baseline-gradient, but the " +
			"LUT is baked off the physically based renderer. Isolates authoring from cost.",
			atmosphere, atmosphereOn: false, aerial, aerialOn: true,
			clouds, PostProcessRendererProfile.SkyOverride.GradientBaked));

		Save(Build("baseline-cubemap", "Cheapest sky: a single static cubemap that cannot " +
			"respond to the sun moving. The literal reading of 'textured skybox'.",
			atmosphere, atmosphereOn: false, aerial, aerialOn: true,
			clouds, PostProcessRendererProfile.SkyOverride.Cubemap));

		Save(Build("nullsky", "Control: the sky pass with a passthrough fragment and no aerial " +
			"perspective. Subtract from noatmo to price the sky pass structure itself.",
			atmosphere, atmosphereOn: false, aerial, aerialOn: false,
			clouds, PostProcessRendererProfile.SkyOverride.Null));

		// The structural twin of the baselines: same two passes, same draw call count, no sky
		// shading. Without it, `baseline - nullsky` silently bundles the whole aerial
		// perspective pass in with the sky shading and reports the sum as the shading cost.
		Save(Build("nullsky-aerial", "Control: the sky pass with a passthrough fragment, plus " +
			"cheap aerial perspective. Structurally identical to the baselines, so subtracting " +
			"it isolates the sky shading alone.",
			atmosphere, atmosphereOn: false, aerial, aerialOn: true,
			clouds, PostProcessRendererProfile.SkyOverride.Null));

		Save(Build("noatmo", "Control: no sky pass at all, and no aerial perspective. This is " +
			"an ablation rather than a baseline - it renders a black sky.",
			atmosphere, atmosphereOn: false, aerial, aerialOn: false,
			clouds, PostProcessRendererProfile.SkyOverride.Off));

		// The cloud series. All three sit on the physically based sky so the only thing varying
		// between them is the cost strategy, and `pbr` above - which has clouds off - is the
		// control they are subtracted from. Without it the cloud cost cannot be separated from
		// the sky's.
		if (clouds != null)
		{
			Save(Build("clouds-full", "Volumetric clouds marching every pixel every frame. The " +
				"upper bound, and the reference image the cheaper modes are judged against.",
				atmosphere, atmosphereOn: true, aerial, aerialOn: false,
				clouds, PostProcessRendererProfile.SkyOverride.Off,
				cloudsOn: true, cloudMode: PostProcessRendererProfile.CloudOverride.Full));

			Save(Build("clouds-half", "Volumetric clouds marching a quarter of the pixels, " +
				"upsampled with a depth-aware filter. Same march, lower resolution.",
				atmosphere, atmosphereOn: true, aerial, aerialOn: false,
				clouds, PostProcessRendererProfile.SkyOverride.Off,
				cloudsOn: true, cloudMode: PostProcessRendererProfile.CloudOverride.Half));

			Save(Build("clouds-temporal", "Volumetric clouds at full resolution with a quarter of " +
				"the pixels marched per frame and the rest reprojected. Cheapest, and the only " +
				"one carrying frame-to-frame state.",
				atmosphere, atmosphereOn: true, aerial, aerialOn: false,
				clouds, PostProcessRendererProfile.SkyOverride.Off,
				cloudsOn: true, cloudMode: PostProcessRendererProfile.CloudOverride.Temporal));
		}

		// The baseline cloud series. All four sit on the physically based sky with the volumetric
		// OFF, so the only thing varying is which cloud renderer runs - and `pbr` above, which has
		// no clouds at all, is the control they are subtracted from.
		Save(Build("clouds-baseline-authored", "Baseline clouds: a relief-shaded texture on two " +
			"spheres, delivered as a post-process so the pass shape matches the volumetric's. The " +
			"simple method RQ1 and RQ2 are phrased against.",
			atmosphere, atmosphereOn: true, aerial, aerialOn: false,
			clouds, PostProcessRendererProfile.SkyOverride.Off,
			cloudsOn: false, cloudMode: PostProcessRendererProfile.CloudOverride.LeaveAlone,
			baselineArm: PostProcessRendererProfile.BaselineCloudOverride.PostAuthored));

		Save(Build("clouds-baseline-baked", "Same shader and same cost as clouds-baseline-authored, " +
			"but the deck textures are baked off the volumetric. The content is then identical, so " +
			"what is left against clouds-full is the technique rather than the art.",
			atmosphere, atmosphereOn: true, aerial, aerialOn: false,
			clouds, PostProcessRendererProfile.SkyOverride.Off,
			cloudsOn: false, cloudMode: PostProcessRendererProfile.CloudOverride.LeaveAlone,
			baselineArm: PostProcessRendererProfile.BaselineCloudOverride.PostBaked));

		Save(Build("clouds-baseline-mesh", "The same baseline shading reached by rasterising a " +
			"sphere per deck rather than a full-screen quad - what a game would actually ship. " +
			"Differs from clouds-baseline-authored only in delivery.",
			atmosphere, atmosphereOn: true, aerial, aerialOn: false,
			clouds, PostProcessRendererProfile.SkyOverride.Off,
			cloudsOn: false, cloudMode: PostProcessRendererProfile.CloudOverride.LeaveAlone,
			baselineArm: PostProcessRendererProfile.BaselineCloudOverride.Mesh));

		Save(Build("clouds-baseline-null", "Control: the cloud pass structure with a passthrough " +
			"fragment. Subtract from pbr to price the target allocation, the two blits and the " +
			"composite that any cloud renderer in this chain pays before shading anything.",
			atmosphere, atmosphereOn: true, aerial, aerialOn: false,
			clouds, PostProcessRendererProfile.SkyOverride.Off,
			cloudsOn: false, cloudMode: PostProcessRendererProfile.CloudOverride.LeaveAlone,
			baselineArm: PostProcessRendererProfile.BaselineCloudOverride.Null));

		// Feature ablations. Each differs from clouds-full by EXACTLY ONE dial, so subtracting gives
		// that feature's cost on its own. Without them a clouds-full number is the sum of four
		// features with no way to decompose it - the same argument that put the null arms in, applied
		// to features rather than to pass structure.
		//
		// They are deliberately not a single "everything off" arm: the four cost time in different
		// shapes - a tap per raymarch step, a whole extra dispatch, a tap per march step, a second
		// density layer - and a combined arm would only give their sum, which is what clouds-full
		// already reports.
		if (clouds != null)
		{
			Save(Build("clouds-full-noshafts", "clouds-full with the light shafts off. Subtract to " +
				"price occluding the atmosphere's in-scatter, which is one texture tap per raymarch " +
				"step across roughly 8.4 million steps in the sky pass alone.",
				atmosphere, atmosphereOn: true, aerial, aerialOn: false,
				clouds, PostProcessRendererProfile.SkyOverride.Off,
				cloudsOn: true, cloudMode: PostProcessRendererProfile.CloudOverride.Full,
				baselineArm: PostProcessRendererProfile.BaselineCloudOverride.Off,
				dials: Dials(shaft: 0)));

			Save(Build("clouds-full-nodecksky", "clouds-full without the deck radiance map. Subtract " +
				"to price a whole extra dispatch - the one that lets the sea and the land see the " +
				"cloud base instead of a clear sky.",
				atmosphere, atmosphereOn: true, aerial, aerialOn: false,
				clouds, PostProcessRendererProfile.SkyOverride.Off,
				cloudsOn: true, cloudMode: PostProcessRendererProfile.CloudOverride.Full,
				baselineArm: PostProcessRendererProfile.BaselineCloudOverride.Off,
				dials: Dials(sky: 0)));

			Save(Build("clouds-full-noaerial", "clouds-full without aerial perspective on the clouds. " +
				"Subtract to price one transmittance tap per cloud march step.",
				atmosphere, atmosphereOn: true, aerial, aerialOn: false,
				clouds, PostProcessRendererProfile.SkyOverride.Off,
				cloudsOn: true, cloudMode: PostProcessRendererProfile.CloudOverride.Full,
				baselineArm: PostProcessRendererProfile.BaselineCloudOverride.Off,
				dials: Dials(aerial: 0)));

			Save(Build("clouds-full-nohighband", "clouds-full without the ice layer. Subtract to price " +
				"the second density layer that carries Cirrus, Cirrocumulus and Cirrostratus - three " +
				"of the ten genera RQ1 scores.",
				atmosphere, atmosphereOn: true, aerial, aerialOn: false,
				clouds, PostProcessRendererProfile.SkyOverride.Off,
				cloudsOn: true, cloudMode: PostProcessRendererProfile.CloudOverride.Full,
				baselineArm: PostProcessRendererProfile.BaselineCloudOverride.Off,
				dials: Dials(high: 0)));
		}

		AssetDatabase.SaveAssets();
		AssetDatabase.Refresh();
		Debug.Log($"[Benchmark] wrote {(clouds != null ? 18 : 11)} renderer profiles to {Folder}\n" +
			"  Assign them to the BenchmarkRunner's Profiles array in the order you want the " +
			"summary's baseline column to be - the first entry is what deltas are measured from.");
	}

	static PostProcessRendererProfile Build(string id, string description,
		AtmosphereEffect atmosphere, bool atmosphereOn,
		AerialPerspectiveSimple aerial, bool aerialOn,
		Clouds.CloudEffect clouds, PostProcessRendererProfile.SkyOverride sky,
		bool cloudsOn = false,
		// Half rather than LeaveAlone as the default. LeaveAlone was harmless while every profile
		// also stated the cloud EFFECT toggle - a cost mode cannot matter if the effect is off - but
		// it is exactly the omission the header warns about, and it becomes live the moment a
		// profile enables clouds without naming a mode.
		PostProcessRendererProfile.CloudOverride cloudMode = PostProcessRendererProfile.CloudOverride.Half,
		PostProcessRendererProfile.BaselineCloudOverride baselineArm =
			PostProcessRendererProfile.BaselineCloudOverride.Off,
		PostProcessRendererProfile.FeatureDials? dials = null)
	{
		var profile = ScriptableObject.CreateInstance<PostProcessRendererProfile>();
		profile.id = id;
		profile.description = description;
		profile.sky = sky;
		profile.clouds = cloudMode;
		// Stated on EVERY profile, and defaulting to Off rather than LeaveAlone. A sky profile that
		// did not mention the baseline clouds would measure the sky with whichever cloud arm the
		// pass before it happened to leave running.
		profile.baselineClouds = baselineArm;

		// Untouched unless this is an ablation arm. Forcing the dials to the scene's values instead
		// would be indistinguishable from leaving them alone until someone retunes the asset, at
		// which point every profile would silently pin the old number.
		profile.dials = dials ?? PostProcessRendererProfile.FeatureDials.Untouched;
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

		// Listed on every profile, not only the cloud ones. An omitted toggle inherits whatever the
		// previous pass left behind, so a sky profile that did not mention clouds would silently
		// measure the sky with whichever clouds the pass before it happened to enable.
		if (clouds != null)
		{
			effects.Add(new PostProcessRendererProfile.EffectToggle { effect = clouds, enabled = cloudsOn });
		}

		profile.effects = effects.ToArray();
		return profile;
	}

	/// <summary>One dial forced, the rest left alone. -1 is "untouched".</summary>
	static PostProcessRendererProfile.FeatureDials Dials(
		float shaft = -1, float sky = -1, float aerial = -1, float high = -1)
	{
		return new PostProcessRendererProfile.FeatureDials
		{
			shaftStrength = shaft,
			skyStrength = sky,
			aerialStrength = aerial,
			highStrength = high
		};
	}

	static void Save(PostProcessRendererProfile profile)
	{
		string path = $"{Folder}/{profile.id}.asset";

		// Two assets predate this generator and are named PBR.asset and NoAtmo.asset while carrying
		// the ids pbr and noatmo. Save() overwrites in place and never renames, so the Inspector has
		// been showing one name while run.json records another. Renamed on sight rather than left,
		// because the mismatch makes a profile hard to find from a result file.
		foreach (string legacy in new[] { "PBR", "NoAtmo" })
		{
			if (!string.Equals(legacy, profile.id, System.StringComparison.OrdinalIgnoreCase)) { continue; }
			string legacyPath = $"{Folder}/{legacy}.asset";
			if (legacyPath != path && AssetDatabase.LoadAssetAtPath<PostProcessRendererProfile>(legacyPath) != null)
			{
				AssetDatabase.RenameAsset(legacyPath, profile.id);
			}
		}

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
