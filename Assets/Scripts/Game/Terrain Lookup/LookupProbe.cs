using UnityEngine;

/// <summary>
/// TEMPORARY diagnostic. Validates that the country index map decodes to the right
/// countries before anything is built on top of it. Delete once green.
///
/// Two tests, both logged once on Start:
///
///   A. Texture reality check. The index map's importer has a default platform cap of
///      2048 with an 8192 Standalone override; if the editor resolves the default, a
///      box-filtered downsample averages neighbouring country indices into garbage. That
///      failure is invisible to test B (large-country interiors survive downsampling), so
///      it has to be checked directly. Also logs the graphics format, because the
///      importer has sRGB ticked on what is index data - harmless in Gamma colour space,
///      wrong the moment anyone switches to Linear.
///
///   B. Ground-truth sweep. Country.cities is capital-first and a capital is by
///      construction inside its own country, so every capital is a free labelled sample.
///
/// Expect ~230+/241 matches. Legitimate misses: coastal capitals whose coordinate lands
/// on an ocean texel, enclaves, and countries with no city data.
/// </summary>
public class LookupProbe : MonoBehaviour
{
	public WorldLookup worldLookup;
	public CountryData countryData;

	[Header("Run")]
	public bool runOnStart = true;
	// Blocking GPU readback per country - fine for a one-shot diagnostic, never per-frame.
	public bool runCapitalSweep = true;
	public int maxMismatchesToList = 25;

	void Start()
	{
		if (runOnStart) { RunAll(); }
	}

	[ContextMenu("Run All Tests")]
	public void RunAll()
	{
		if (!TestA()) { return; }
		if (runCapitalSweep) { TestB(); }
	}

	[ContextMenu("Test A: texture reality check")]
	public bool TestA()
	{
		if (worldLookup == null) { worldLookup = GetComponent<WorldLookup>(); }

		if (worldLookup == null || countryData == null)
		{
			Debug.LogError("LookupProbe: assign worldLookup and countryData.", this);
			return false;
		}

		Texture2D tex = worldLookup.countryIndices;
		if (tex == null)
		{
			Debug.LogError("LookupProbe: WorldLookup.countryIndices is not assigned.", this);
			return false;
		}

		Debug.Log(
			$"[Probe A] index map {tex.width}x{tex.height} (expect 8192x4096)\n" +
			$"          format {tex.graphicsFormat} (expect R8_UNorm; R8_SRGB = untick sRGB on the importer)\n" +
			$"          filter {tex.filterMode} (expect Point)   readable {tex.isReadable}\n" +
			$"          colour space {QualitySettings.activeColorSpace} (expect Gamma)\n" +
			$"          asyncReadback {SystemInfo.supportsAsyncGPUReadback}\n" +
			$"          countries {countryData.NumCountries} (expect 241)", this);

		bool ok = true;
		if (tex.width < 8192)
		{
			// Hard stop: averaging country indices produces values that decode to
			// unrelated countries, which would poison every feature downstream.
			Debug.LogError($"[Probe A] FAIL: index map is {tex.width}px wide, not 8192. The editor is " +
				"not applying the Standalone maxTextureSize override. Raise the default platform " +
				"maxTextureSize to 8192 on Country Index Map.png before going further.", this);
			ok = false;
		}
		if (tex.graphicsFormat.ToString().Contains("SRGB"))
		{
			Debug.LogError($"[Probe A] FAIL: index map imported as {tex.graphicsFormat}. Untick " +
				"'sRGB (Color Texture)' on Country Index Map.png - gamma decode corrupts index values.", this);
			ok = false;
		}
		if (tex.filterMode != FilterMode.Point)
		{
			Debug.LogError($"[Probe A] FAIL: filter mode is {tex.filterMode}, must be Point. " +
				"Interpolating between two country indices yields a third, unrelated country.", this);
			ok = false;
		}

		Debug.Log(ok ? "[Probe A] PASS" : "[Probe A] FAILED - fix the above before running Test B.", this);
		return ok;
	}

	[ContextMenu("Test B: capital-city ground-truth sweep")]
	public void TestB()
	{
		Country[] countries = countryData.Countries;
		int matched = 0, mismatched = 0, noCity = 0, ocean = 0;
		var mismatches = new System.Text.StringBuilder();
		int listed = 0;

		for (int i = 0; i < countries.Length; i++)
		{
			Country c = countries[i];
			if (c.cities == null || c.cities.Length == 0) { noCity++; continue; }

			TerrainInfo info = worldLookup.GetTerrainInfoImmediate(c.cities[0].coordinate);

			if (info.countryIndex == i)
			{
				matched++;
			}
			else if (info.inOcean)
			{
				ocean++;
				if (listed < maxMismatchesToList)
				{
					mismatches.AppendLine($"    [{i}] {c.name} ({c.alpha3Code}) capital '{c.cities[0].name}' -> OCEAN");
					listed++;
				}
			}
			else
			{
				mismatched++;
				string got = (info.countryIndex >= 0 && info.countryIndex < countries.Length)
					? $"{countries[info.countryIndex].name} [{info.countryIndex}]"
					: $"OUT OF RANGE [{info.countryIndex}]";
				if (listed < maxMismatchesToList)
				{
					mismatches.AppendLine($"    [{i}] {c.name} ({c.alpha3Code}) capital '{c.cities[0].name}' -> {got}");
					listed++;
				}
			}
		}

		int tested = matched + mismatched + ocean;
		float pct = tested > 0 ? 100f * matched / tested : 0f;

		Debug.Log(
			$"[Probe B] capital sweep: {matched}/{tested} matched ({pct:0.0}%)\n" +
			$"          wrong country {mismatched}   landed on ocean {ocean}   no city data {noCity}\n" +
			(mismatches.Length > 0 ? $"          first {listed}:\n{mismatches}" : ""), this);

		if (pct >= 90f)
		{
			Debug.Log("[Probe B] PASS - decode and index alignment are sound. " +
				"Remaining misses are expected (coastal capitals on ocean texels, enclaves).", this);
		}
		else if (matched < tested * 0.1f)
		{
			Debug.LogError("[Probe B] FAIL, near-total mismatch. If returned indices are biased " +
				"toward 0, sRGB decode is being applied. If wrong with no pattern, the " +
				"equirectangular convention is mismatched between Coordinate.ToUV() and " +
				"CountryIndexMapper.compute:54-55.", this);
		}
		else
		{
			Debug.LogWarning($"[Probe B] INCONCLUSIVE at {pct:0.0}%. Inspect the listed mismatches " +
				"before building on this.", this);
		}
	}
}
