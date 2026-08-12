using UnityEditor;
using UnityEngine;

using Arm = PostProcessRendererProfile.AtmosphereArm;
using Cloud = PostProcessRendererProfile.CloudArm;

/// <summary>
/// Authors the renderer profiles, as three named sets.
///
/// A profile is now a PAIR - one atmosphere arm, one cloud arm - so this file no longer hand-writes
/// combinations of toggles and no profile can inherit a switch by omission. That was the whole
/// hazard the old set was arranged around, and it is gone by construction rather than by discipline.
///
/// Three sets, because they answer different questions and only one of them belongs in a run you
/// would put in the report:
///
///   Report      the renderers a reader cares about. Ten passes, and the thesis table maps onto it
///               one-to-one.
///   Controls    null arms and the mesh delivery. Instruments, not results: they exist so a number
///               can be decomposed into pass structure versus shading.
///   Ablations   one volumetric feature turned off apiece, so each can be priced on its own.
///
/// Point the BenchmarkRunner at whichever set you are running. F5's live preview cycles that same
/// array, so the interactive renderer swap and the measured one cannot disagree - they are the same
/// assets.
///
/// The subtraction each set supports:
///
///   controls/atmos-null      - report/null            the sky pass structure
///   controls/atmos-null-ap   - controls/atmos-null    the cheap aerial perspective pass
///   report/base-atmos        - controls/atmos-null-ap the cheap sky shading alone
///   report/pbr-atmos         - report/base-atmos      the headline RQ2 sky number
///   controls/clouds-null     - report/pbr-atmos       the cloud pass structure
///   report/...base-clouds-*  - controls/clouds-null   the cheap cloud shading model
///   report/...pbr-clouds-*   - controls/clouds-null   the march
///   ablations/*              - report/pbr-clouds-full one feature apiece
/// </summary>
static class RendererProfiles
{
	const string Root = "Assets/Data/RenderingProfiles";

	[MenuItem("Testbed/Benchmark/Create Renderer Profiles")]
	static void Create()
	{
		// --- Report: the table in the thesis --------------------------------------------------
		//
		// Ordered cheapest-first so the runner's delta column reads down the page, and so the first
		// entry - which is what deltas are measured from - is the arm with nothing in it.
		Save("Report", "null", Arm.Off, Cloud.Off,
			"Nothing draws a sky and there are no clouds. The floor everything else is measured from.");

		Save("Report", "base-atmos", Arm.GradientBaked, Cloud.Off,
			"Cheap sky from a LUT baked off the physically based renderer, plus exponential fog. " +
			"Baked rather than hand-authored so the sky comparison is of the technique, not the art.");

		Save("Report", "base-atmos+base-clouds-authored", Arm.GradientBaked, Cloud.BaselineAuthored,
			"The all-cheap arm: textured sky, textured clouds, both authored. What a strategy game " +
			"ships today.");

		Save("Report", "base-atmos+base-clouds-baked", Arm.GradientBaked, Cloud.BaselineBaked,
			"The same, with cloud textures baked off the volumetric. Same cost as the authored arm, " +
			"so any difference between the two is content rather than technique.");

		Save("Report", "pbr-atmos", Arm.Pbr, Cloud.Off,
			"Raymarched sky with multiple scattering and scattering-LUT aerial perspective, no " +
			"clouds. The control every cloud arm subtracts from.");

		Save("Report", "pbr-atmos+base-clouds-authored", Arm.Pbr, Cloud.BaselineAuthored,
			"Physically based sky over cheap clouds - the mixed arm, and the one a project with a " +
			"cloud budget but not a sky budget would actually pick.");

		Save("Report", "pbr-atmos+base-clouds-baked", Arm.Pbr, Cloud.BaselineBaked,
			"The same with baked cloud content, so the cloud comparison against the volumetric arms " +
			"is technique-only.");

		Save("Report", "pbr-atmos+pbr-clouds-half", Arm.Pbr, Cloud.VolumetricHalf,
			"Volumetric clouds marching a quarter of the pixels, upsampled with a depth-aware " +
			"filter. Same march, lower resolution.");

		Save("Report", "pbr-atmos+pbr-clouds-temporal", Arm.Pbr, Cloud.VolumetricTemporal,
			"Full resolution with a quarter of the pixels marched per frame and the rest " +
			"reprojected. Cheapest, and the only arm carrying frame-to-frame state.");

		Save("Report", "pbr-atmos+pbr-clouds-full", Arm.Pbr, Cloud.VolumetricFull,
			"Every pixel marched every frame. The honest upper bound and the reference image the " +
			"cheaper modes are judged against.");

		// --- Controls: instruments, not results -----------------------------------------------
		//
		// Some of what a pass costs is not shading: a render target, two blits, the bandwidth of
		// writing and reading a buffer. Subtracting these is what turns a frame time into a claim
		// about a technique.
		Save("Controls", "atmos-null", Arm.Null, Cloud.Off,
			"The sky pass with a passthrough fragment and no aerial perspective. Subtract from " +
			"report/null to price the sky pass structure itself.");

		Save("Controls", "atmos-null-ap", Arm.NullWithAerial, Cloud.Off,
			"The null sky plus cheap aerial perspective - structurally identical to the baseline " +
			"arms, so subtracting it isolates the sky shading alone. Without it, base-atmos minus " +
			"atmos-null bundles the whole aerial perspective pass in with the shading.");

		Save("Controls", "clouds-null", Arm.Pbr, Cloud.Null,
			"The cloud pass structure with a passthrough fragment. Subtract from report/pbr-atmos " +
			"to price the target allocation, the two blits and the composite that any cloud " +
			"renderer in this chain pays before shading anything.");

		Save("Controls", "base-clouds-mesh", Arm.Pbr, Cloud.BaselineMesh,
			"The baseline's shading reached by rasterising a sphere per deck rather than a " +
			"full-screen quad. Differs from the post-process arm only in delivery, so the gap is " +
			"what the delivery mechanism is worth.");

		// --- Ablations: one volumetric feature apiece -----------------------------------------
		//
		// Each differs from report/pbr-atmos+pbr-clouds-full by exactly one dial. Deliberately not
		// one combined "everything off" arm, which would only report their sum - which the full arm
		// already does.
		Save("Ablations", "no-shafts", Arm.Pbr, Cloud.VolumetricFull,
			"Light shafts off. One texture tap per raymarch step, across roughly 8.4 million steps " +
			"in the sky pass alone.", Dials(shaft: 0));

		Save("Ablations", "no-deck-sky", Arm.Pbr, Cloud.VolumetricFull,
			"The deck radiance map off. A whole extra dispatch - the one that lets the sea and the " +
			"land see the cloud base instead of a clear sky.", Dials(sky: 0));

		Save("Ablations", "no-cloud-aerial", Arm.Pbr, Cloud.VolumetricFull,
			"Aerial perspective on the clouds off. One transmittance tap per cloud march step.",
			Dials(aerial: 0));

		Save("Ablations", "no-high-band", Arm.Pbr, Cloud.VolumetricFull,
			"The ice layer off. A second density layer, carrying the three genera - Cirrus, " +
			"Cirrocumulus, Cirrostratus - that the low band cannot reach.", Dials(high: 0));

		AssetDatabase.SaveAssets();
		AssetDatabase.Refresh();

		Debug.Log("[Benchmark] wrote 18 renderer profiles under " + Root + "\n" +
			"  Report/ 10, Controls/ 4, Ablations/ 4. Point the BenchmarkRunner's Profiles array at " +
			"one set - F5's live preview cycles the same array, so the interactive swap and the " +
			"measured one are the same assets.\n" +
			"  Profiles from the previous flat layout are now orphaned and can be deleted once the " +
			"runner no longer references them.");
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

	static void Save(string set, string id, Arm atmosphere, Cloud clouds, string description,
		PostProcessRendererProfile.FeatureDials? dials = null)
	{
		string folder = $"{Root}/{set}";
		System.IO.Directory.CreateDirectory(folder);

		var profile = ScriptableObject.CreateInstance<PostProcessRendererProfile>();
		profile.id = id;
		profile.description = description;
		profile.atmosphereArm = atmosphere;
		profile.cloudArm = clouds;
		profile.objects = new PostProcessRendererProfile.ObjectToggle[0];

		// Untouched unless this is an ablation arm. Pinning the dials to the scene's current values
		// instead would be indistinguishable from leaving them alone right up until someone retunes
		// the asset, at which point every profile would silently hold the old number.
		profile.dials = dials ?? PostProcessRendererProfile.FeatureDials.Untouched;

		string path = $"{folder}/{id}.asset";

		// Overwrite in place: CreateAsset mints a new GUID, which would null out every reference to
		// the profile - most obviously the BenchmarkRunner's Profiles array - each time this menu
		// item is used. Regenerating the set is meant to be routine.
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

		profile.name = id;
		AssetDatabase.CreateAsset(profile, path);
	}
}
