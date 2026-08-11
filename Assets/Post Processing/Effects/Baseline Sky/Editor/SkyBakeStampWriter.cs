using System.Globalization;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor side of <see cref="SkyBakeStamp"/>: records what a bake was made from.
///
/// Kept apart from the runtime class so the asset creation and AssetDatabase calls do not
/// have to be `#if UNITY_EDITOR`-fenced inside it.
/// </summary>
static class SkyBakeStampWriter
{
	const string Folder = "Assets/Resources";
	const string StampPath = Folder + "/SkyBakeStamp.asset";

	/// <summary>
	/// Stamps a baked asset with the hash of the scene values it was derived from, plus the
	/// baker's own constants in readable form for diagnosis.
	///
	/// `bakeConstants` is recorded but not hashed - see the Recipe docs for why.
	/// </summary>
	public static void Record(string assetPath, SkyBakeStamp.Recipe recipe,
		AtmosphereEffect atmosphere, BaselineSkyRenderer baseline, SkyBakeStamp.Inputs bakeConstants,
		Clouds.CloudEffect clouds = null)
	{
		SkyBakeStamp stamp = LoadOrCreate();

		SkyBakeStamp.Inputs scene = SkyBakeStamp.InputsFor(recipe, atmosphere, baseline, clouds);

		var entry = new SkyBakeStamp.Entry
		{
			assetPath = assetPath,
			recipe = recipe,
			hash = scene.Hash(),
			bakedUtc = System.DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture),
			inputs = scene.Text + (bakeConstants != null ? bakeConstants.Text : "")
		};

		var entries = new System.Collections.Generic.List<SkyBakeStamp.Entry>(stamp.entries);
		entries.RemoveAll(e => e.assetPath == assetPath);
		entries.Add(entry);
		stamp.entries = entries.ToArray();

		EditorUtility.SetDirty(stamp);
		AssetDatabase.SaveAssets();
		SkyBakeStamp.Invalidate();
	}

	static SkyBakeStamp LoadOrCreate()
	{
		var stamp = AssetDatabase.LoadAssetAtPath<SkyBakeStamp>(StampPath);
		if (stamp != null) { return stamp; }

		System.IO.Directory.CreateDirectory(Folder);
		stamp = ScriptableObject.CreateInstance<SkyBakeStamp>();
		AssetDatabase.CreateAsset(stamp, StampPath);
		return stamp;
	}
}
