using UnityEngine;

/// <summary>
/// Baked per-country label placement. Entries are index-aligned with
/// <see cref="CountryData.Countries"/>, so a country index from
/// <see cref="WorldLookup"/> indexes straight into this.
///
/// Baked offline (see CountryLabelBaker) because finding the pole of inaccessibility is a
/// brute-force search over every polygon edge - fine as a one-off editor pass, not
/// something to do at load.
/// </summary>
[CreateAssetMenu(menuName = "Testbed/Country Label Data", fileName = "Country Label Data")]
public class CountryLabelData : ScriptableObject
{
	[System.Serializable]
	public struct Entry
	{
		// Checked against CountryData on load: if these drift, the baked asset is stale
		// and every label is attached to the wrong country.
		public string alpha3Code;
		// Baked so the runtime never has to run GetPreferredDisplayName's scoring.
		public string displayName;
		// Pole of inaccessibility: the point furthest from any border, inside the
		// country's largest polygon.
		public Coordinate anchor;
		// Angular radius in radians of the largest circle centred on the anchor that
		// stays inside the country. Doubles as a size measure for the label filter.
		public float angularRadius;
		// The gnomonic projection could not cover this country's largest polygon, so the
		// anchor fell back to a centroid and may sit outside the shape.
		public bool bakeFailed;
	}

	public CountryData source;
	public Entry[] entries;

	public int Count => entries != null ? entries.Length : 0;

	public bool TryGetEntry(int index, out Entry entry)
	{
		if (entries == null || index < 0 || index >= entries.Length)
		{
			entry = default;
			return false;
		}
		entry = entries[index];
		return true;
	}

	/// <summary>
	/// Confirms the baked entries still line up with the country data they were baked
	/// from. Cheap, and the failure it catches is otherwise silent and very confusing.
	/// </summary>
	public bool ValidateAlignment(CountryData countryData, out string error)
	{
		error = null;
		if (countryData == null) { error = "no CountryData supplied"; return false; }
		if (entries == null) { error = "label data has not been baked"; return false; }

		Country[] countries = countryData.Countries;
		if (countries == null || countries.Length != entries.Length)
		{
			error = $"baked {Count} entries but CountryData has " +
				$"{(countries == null ? 0 : countries.Length)} countries - re-bake";
			return false;
		}

		for (int i = 0; i < entries.Length; i++)
		{
			if (entries[i].alpha3Code != countries[i].alpha3Code)
			{
				error = $"entry {i} is '{entries[i].alpha3Code}' but CountryData has " +
					$"'{countries[i].alpha3Code}' - re-bake";
				return false;
			}
		}
		return true;
	}
}
