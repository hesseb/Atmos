using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

/// <summary>
/// Records what each baked sky asset was baked from, so a stale one can be detected.
///
/// The bakes are derived assets and **nothing else notices when their inputs move on**. There
/// is no error and no visual cue - just a baseline that quietly no longer corresponds to the
/// renderer it was derived from. For `baseline-baked`, whose entire premise is "the physically
/// based sky, flattened", a stale bake makes the RQ1 comparison measure the staleness rather
/// than the technique.
///
/// This already happened once: correcting the tone-map constants silently invalidated both
/// gradients until they were regenerated. The atmosphere work now underway invalidates them
/// repeatedly, which is why this exists rather than a note asking someone to remember.
///
/// Same idea as `plan_hash` and `pose_hash` in the benchmark harness: hash the inputs, compare
/// later, and make a mismatch loud.
/// </summary>
public class SkyBakeStamp : ScriptableObject
{
	public const string ResourcePath = "SkyBakeStamp";

	/// <summary>
	/// Which scene values a baked asset depends on. Only these enter the hash.
	///
	/// The bakers' own constants - altitude, azimuth, step counts - are recorded in the
	/// readable text for diagnosis but deliberately left out of the hash: they change only by
	/// editing the baker, which is visible in the diff, whereas scene parameters change by
	/// dragging a slider and leave no trace at all.
	/// </summary>
	public enum Recipe
	{
		/// <summary>Depends on the atmosphere's parameters. The baked gradient and the cubemap
		/// store raw radiance, so the tone map is applied at runtime and does not stale them.</summary>
		Atmosphere,
		/// <summary>Depends only on the tone-map constants: the hand-authored gradient is
		/// inverse-mapped against them, so it goes stale when they move even though no
		/// atmosphere parameter changed.</summary>
		ToneMap
	}

	[System.Serializable]
	public struct Entry
	{
		/// <summary>Project-relative path of the baked asset this describes.</summary>
		public string assetPath;
		public Recipe recipe;
		public string hash;
		public string bakedUtc;
		/// <summary>The inputs in readable form, so a mismatch says *what* changed rather than
		/// only that something did.</summary>
		[TextArea(2, 6)] public string inputs;
	}

	public Entry[] entries = new Entry[0];

	static SkyBakeStamp cached;
	static bool loadAttempted;

	/// <summary>Null if no bake has ever been stamped.</summary>
	public static SkyBakeStamp Load()
	{
		if (loadAttempted) { return cached; }

		loadAttempted = true;
		cached = Resources.Load<SkyBakeStamp>(ResourcePath);
		return cached;
	}

	/// <summary>Forces the next Load to hit disk. Called by the bakers after writing.</summary>
	public static void Invalidate() { loadAttempted = false; cached = null; }

	public bool TryGet(string assetPath, out Entry entry)
	{
		foreach (Entry candidate in entries)
		{
			if (candidate.assetPath == assetPath) { entry = candidate; return true; }
		}
		entry = default;
		return false;
	}

	// ------------------------------------------------------------------ hashing

	/// <summary>
	/// Accumulates the named values a bake depends on, and hashes them.
	///
	/// Values are formatted round-trip ("R") rather than quantised: these are authored
	/// constants, not measured quantities, so an exact comparison is what is wanted. A
	/// parameter that changes at all invalidates the bake.
	/// </summary>
	public class Inputs
	{
		static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
		readonly StringBuilder text = new StringBuilder();

		public Inputs Add(string name, float value)
		{
			text.Append(name).Append('=').Append(value.ToString("R", Ci)).Append('\n');
			return this;
		}

		public Inputs Add(string name, int value)
		{
			text.Append(name).Append('=').Append(value.ToString(Ci)).Append('\n');
			return this;
		}

		public Inputs Add(string name, Vector3 value)
		{
			return Add(name + ".x", value.x).Add(name + ".y", value.y).Add(name + ".z", value.z);
		}

		public Inputs Add(string name, Vector2Int value)
		{
			return Add(name + ".x", value.x).Add(name + ".y", value.y);
		}

		public string Text => text.ToString();

		/// <summary>FNV-1a 64, the same construction the benchmark hashes use.</summary>
		public string Hash()
		{
			ulong hash = 0xcbf29ce484222325UL;
			foreach (char c in text.ToString())
			{
				hash ^= c;
				hash *= 0x100000001b3UL;
			}
			return "0x" + hash.ToString("x16");
		}
	}

	/// <summary>
	/// Everything the physically based sky's output depends on. Shared by both bakers so they
	/// cannot disagree about what counts as an input - and so adding a parameter to the
	/// atmosphere later means adding it here once.
	/// </summary>
	public static Inputs AtmosphereInputs(AtmosphereEffect a)
	{
		return new Inputs()
			.Add("bodyRadius", a.bodyRadius)
			.Add("atmosphereThickness", a.atmosphereThickness)
			.Add("wavelengthsRGB", a.wavelengthsRGB)
			.Add("wavelengthScale", a.wavelengthScale)
			.Add("rayleighDensityAvg", a.rayleighDensityAvg)
			.Add("mieCoefficient", a.mieCoefficient)
			.Add("mieDensityAvg", a.mieDensityAvg)
			.Add("mieAbsorption", a.mieAbsorption)
			.Add("mieAsymmetry", a.mieAsymmetry)
			.Add("sunIlluminance", a.sunIlluminance)
			.Add("ozonePeakDensityAltitude", a.ozonePeakDensityAltitude)
			.Add("ozoneDensityFalloff", a.ozoneDensityFalloff)
			.Add("ozoneStrength", a.ozoneStrength)
			.Add("ozoneAbsorption", a.ozoneAbsorption)
			.Add("transmittanceLUTSize", a.transmittanceLUTSize)
			.Add("multipleScatteringStrength", a.multipleScatteringStrength)
			.Add("multipleScatteringLUTSize", a.multipleScatteringLUTSize)
			.Add("groundAlbedo", a.groundAlbedo);
	}

	/// <summary>The tone-map constants the hand-authored gradient is inverse-mapped against.</summary>
	public static Inputs ToneMapInputs(BaselineSkyRenderer b)
	{
		return new Inputs()
			.Add("intensity", b != null ? b.intensity : 1f)
			.Add("contrast", b != null ? b.contrast : 1.45f)
			.Add("whitePoint", b != null ? b.whitePoint : 1.1f);
	}

	/// <summary>The scene-derived inputs for a recipe. One place, so bakers and the staleness
	/// check cannot disagree about what an asset depends on.</summary>
	public static Inputs InputsFor(Recipe recipe, AtmosphereEffect atmosphere, BaselineSkyRenderer baseline)
	{
		return recipe == Recipe.ToneMap ? ToneMapInputs(baseline) : AtmosphereInputs(atmosphere);
	}

	/// <summary>
	/// Checks every stamped asset against the live scene and reports what is stale.
	///
	/// Returns descriptions rather than logging, so the benchmark harness can fold them into
	/// `run.json`'s warnings where they end up next to the numbers they invalidate.
	/// </summary>
	public static List<string> FindStale(AtmosphereEffect atmosphere, BaselineSkyRenderer baseline)
	{
		var stale = new List<string>();
		SkyBakeStamp stamp = Load();
		if (stamp == null || atmosphere == null) { return stale; }

		foreach (Entry entry in stamp.entries)
		{
			if (InputsFor(entry.recipe, atmosphere, baseline).Hash() != entry.hash)
			{
				stale.Add($"{entry.assetPath} (baked {entry.bakedUtc})");
			}
		}
		return stale;
	}
}
