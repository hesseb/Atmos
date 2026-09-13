using UnityEditor;
using UnityEngine;

using Atmosphere = PostProcessRendererProfile.AtmosphereArm;
using Cloud = PostProcessRendererProfile.CloudArm;

static class RendererProfiles
{
	const string Root = "Assets/Profiles/RenderingProfiles";

	[MenuItem( "Testbed/Benchmark/Create Renderer Profiles" )]
	static void Create()
	{
		// Report
		Save( "Report", "null", Atmosphere.Off, Cloud.Off,
			"No atmosphere, no clouds." );

		Save( "Report", "base-atmos", Atmosphere.GradientBaked, Cloud.Off,
			"Baked atmosphere, no clouds." );

		Save( "Report", "base-atmos-clouds-authored", Atmosphere.GradientBaked, Cloud.BaselineAuthored,
			"Baked atmosphere, baseline authored clouds" );

		Save( "Report", "base-atmos-clouds-baked", Atmosphere.GradientBaked, Cloud.BaselineBaked,
			"Baked atmosphere, baked clouds" );

		Save( "Report", "pbr-atmos", Atmosphere.Pbr, Cloud.Off,
			"PBR Atmosphere, no clouds." );

		Save( "Report", "pbr-atmos-base-clouds-authored", Atmosphere.Pbr, Cloud.BaselineAuthored,
			"PBR Atmosphere, baseline authored clouds" );

		Save( "Report", "pbr-atmos-base-clouds-baked", Atmosphere.Pbr, Cloud.BaselineBaked,
			"PBR Atmosphere, baked clouds" );

		Save( "Report", "pbr-atmos-clouds-half", Atmosphere.Pbr, Cloud.VolumetricHalf,
			"PBR Atmosphere, PBR clouds Half Resolution." );

		Save( "Report", "pbr-atmos-clouds-temporal", Atmosphere.Pbr, Cloud.VolumetricTemporal,
			"PBR Atmosphere, PBR Clouds Temporal." );

		Save( "Report", "pbr-atmos-clouds-full", Atmosphere.Pbr, Cloud.VolumetricFull,
			"PBR Atmosphere, PBR Clouds Full Resolution." );

		// Controls
		Save( "Controls", "atmos-null", Atmosphere.Null, Cloud.Off,
			"No atmosphere, no clouds." );

		Save( "Controls", "atmos-null-ap", Atmosphere.NullWithAerial, Cloud.Off,
			"Null sky plus aerial perspective, no clouds." );

		Save( "Controls", "clouds-null", Atmosphere.Pbr, Cloud.Null,
			"No clouds." );

		Save( "Controls", "base-clouds", Atmosphere.Pbr, Cloud.BaselineMesh,
			"PBR atmosphere, baseline clouds." );

		// Individual features
		Save( "Features", "no-shafts", Atmosphere.Pbr, Cloud.VolumetricFull,
			"Light shafts off.", Dials( shaft: 0 ) );

		Save( "Features", "no-deck-sky", Atmosphere.Pbr, Cloud.VolumetricFull,
			"The deck radiance map off (what lets the sea and land know about the clouds).", Dials( sky: 0 ) );

		Save( "Features", "no-cloud-aerial", Atmosphere.Pbr, Cloud.VolumetricFull,
			"No Aerial perspective on the clouds.",
			Dials( aerial: 0 ) );

		Save( "Features", "no-cloud-high-band", Atmosphere.Pbr, Cloud.VolumetricFull,
			"No cloud high band.", Dials( high: 0 ) );

		AssetDatabase.SaveAssets();
		AssetDatabase.Refresh();

		Debug.Log( "[Benchmark] Renderer profiles created under " + Root );
	}

	static PostProcessRendererProfile.FeatureDials Dials(
		float shaft = -1, float sky = -1, float aerial = -1, float high = -1 )
	{
		return new PostProcessRendererProfile.FeatureDials
		{
			shaftStrength = shaft,
			skyStrength = sky,
			aerialStrength = aerial,
			highStrength = high
		};
	}

	static void Save( string set, string id, Atmosphere atmosphere, Cloud clouds, string description,
		PostProcessRendererProfile.FeatureDials? dials = null )
	{
		string folder = $"{Root}/{set}";
		System.IO.Directory.CreateDirectory( folder );

		var profile = ScriptableObject.CreateInstance<PostProcessRendererProfile>();
		profile.id = id;
		profile.description = description;
		profile.atmosphereArm = atmosphere;
		profile.cloudArm = clouds;
		profile.objects = new PostProcessRendererProfile.ObjectToggle[ 0 ];

		profile.dials = dials ?? PostProcessRendererProfile.FeatureDials.Untouched;

		string path = $"{folder}/{id}.asset";

		var existing = AssetDatabase.LoadAssetAtPath<PostProcessRendererProfile>( path );
		if ( existing != null )
		{
			profile.name = existing.name;
			EditorUtility.CopySerialized( profile, existing );
			EditorUtility.SetDirty( existing );
			Object.DestroyImmediate( profile );
			return;
		}

		profile.name = id;
		AssetDatabase.CreateAsset( profile, path );
	}
}
