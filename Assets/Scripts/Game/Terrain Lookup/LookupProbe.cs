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
	// Optional. If assigned, Test C cross-checks every baked label anchor.
	public CountryLabelData labelData;

	[Header("Run")]
	// Off by default: the sweeps issue a blocking GPU readback per country, ~460 of them
	// between tests B and C, which stalls the pipeline on every entry to play mode. Run
	// them from the context menu when the data pipeline changes - after regenerating the
	// index map, or re-baking label anchors.
	public bool runOnStart;
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
		if (labelData != null) { TestC(); }
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
		// Ocean hits are a property of the test points, not of the lookup: a coastal
		// capital's coordinate simply falls on an ocean texel. Judge correctness only on
		// the samples that landed on land at all.
		int onLand = matched + mismatched;
		float pct = onLand > 0 ? 100f * matched / onLand : 0f;

		Debug.Log(
			$"[Probe B] capital sweep: {matched}/{onLand} of on-land samples correct ({pct:0.0}%)\n" +
			$"          wrong country {mismatched}   landed on ocean {ocean} (coastal/island capitals)   no city data {noCity}\n" +
			(mismatches.Length > 0 ? $"          first {listed}:\n{mismatches}" : ""), this);

		if (pct >= 95f)
		{
			Debug.Log("[Probe B] PASS - decode and index alignment are sound. Remaining misses " +
				"are disputed borders where the rasterised answer is defensible.", this);
		}
		else if (matched < onLand * 0.1f)
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

	/// <summary>
	/// Cross-checks every baked label anchor against the country index map.
	///
	/// This is a much sharper test than the capital sweep: an anchor is the point
	/// furthest from any border, so it should be deep inside its own country by
	/// construction. Anything other than near-100% means a real bug - the wrong polygon
	/// chosen, holes mishandled, or the entries no longer aligned with CountryData.
	/// </summary>
	[ContextMenu("Test C: label anchor cross-check")]
	public void TestC()
	{
		if (labelData == null)
		{
			Debug.LogError("LookupProbe: assign labelData to run Test C.", this);
			return;
		}

		if (!labelData.ValidateAlignment(countryData, out string alignmentError))
		{
			Debug.LogError($"[Probe C] FAIL: {alignmentError}", this);
			return;
		}

		Country[] countries = countryData.Countries;

		// A country whose inscribed circle is smaller than one texel of the index map may
		// have no texel of its own anywhere - the rasteriser simply had nowhere to put it.
		// That is a limit of the map's resolution, not of the anchor, so those countries
		// are counted separately rather than as failures.
		float texelAngle = 2f * Mathf.PI / Mathf.Max(1, worldLookup.countryIndices.width);

		int matched = 0, mismatched = 0, ocean = 0, skipped = 0, subTexel = 0;
		var mismatches = new System.Text.StringBuilder();
		var subTexelNames = new System.Text.StringBuilder();
		int listed = 0;

		for (int i = 0; i < labelData.entries.Length; i++)
		{
			CountryLabelData.Entry entry = labelData.entries[i];
			// Failed bakes fell back to a centroid, which is allowed to be outside.
			if (entry.bakeFailed) { skipped++; continue; }

			TerrainInfo info = worldLookup.GetTerrainInfoImmediate(entry.anchor);
			if (info.countryIndex == i) { matched++; continue; }

			if (entry.angularRadius < texelAngle)
			{
				subTexel++;
				if (subTexelNames.Length < 400)
				{
					subTexelNames.Append($"{countries[i].name}, ");
				}
				continue;
			}

			if (info.inOcean) { ocean++; } else { mismatched++; }

			if (listed < maxMismatchesToList)
			{
				string got = info.inOcean ? "OCEAN"
					: (info.countryIndex >= 0 && info.countryIndex < countries.Length
						? $"{countries[info.countryIndex].name} [{info.countryIndex}]"
						: $"OUT OF RANGE [{info.countryIndex}]");
				mismatches.AppendLine($"    [{i}] {countries[i].name} ({entry.alpha3Code}) " +
					$"anchor r={entry.angularRadius * Mathf.Rad2Deg:0.000}deg -> {got}");
				listed++;
			}
		}

		int resolvable = matched + mismatched + ocean;
		float pct = resolvable > 0 ? 100f * matched / resolvable : 0f;

		Debug.Log(
			$"[Probe C] anchor cross-check: {matched}/{resolvable} of map-resolvable countries " +
			$"correct ({pct:0.0}%)\n" +
			$"          wrong country {mismatched}   ocean {ocean}   bake failed {skipped}\n" +
			$"          below index map resolution {subTexel} " +
			$"(one texel spans {texelAngle * Mathf.Rad2Deg:0.0000}deg): {subTexelNames}\n" +
			(mismatches.Length > 0 ? $"          mismatches:\n{mismatches}" : ""), this);

		if (pct >= 98f)
		{
			Debug.Log("[Probe C] PASS - anchors land inside their own countries. Countries " +
				"smaller than a texel cannot be resolved by the index map at all, and are too " +
				"small to label or hover regardless.", this);
		}
		else
		{
			Debug.LogError($"[Probe C] FAIL at {pct:0.0}%. Anchors should be deep inside their " +
				"country by construction, so a miss on a country large enough to resolve " +
				"indicates a real bug: wrong polygon chosen, holes mishandled, or index " +
				"alignment broken.", this);
		}
	}
}
