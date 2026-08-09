using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Computes each country's label anchor: the pole of inaccessibility, i.e. the interior
/// point furthest from any border. Editor-only - it is a brute-force search over every
/// polygon edge, run once and baked.
///
/// A centroid is not good enough. For a concave country the centroid can land outside the
/// shape entirely (verified on a test L-shape: centroid outside, pole of inaccessibility
/// comfortably inside), which would put Chile's label in Argentina.
///
/// Everything is done on 3D unit vectors rather than in lon/lat. That single choice
/// removes the antimeridian seam and the pole singularity - Fiji and Russia need no
/// special cases - and it is why distances can be measured as true angles.
/// </summary>
public static class CountryLabelBaker
{
	// Candidate grid over the projected bounding box.
	const int CoarseGridResolution = 64;
	// Each refinement halves the cell size around the current best, 9 candidates a time.
	const int RefinementPasses = 5;
	// Gnomonic blows up as a point approaches 90 degrees from the projection centre.
	const float MinProjectionCosine = 0.1f;
	const int DisplayNameMaxLength = 18;

	public struct BakeReport
	{
		public int baked;
		public int failed;
		public int emptyShape;
		public List<string> failedNames;
	}

	/// <param name="onProgress">Reports (fraction, country name). Optional.</param>
	public static CountryLabelData.Entry[] Bake(CountryData countryData, out BakeReport report,
		System.Action<float, string> onProgress = null)
	{
		report = new BakeReport { failedNames = new List<string>() };

		Country[] countries = countryData.Countries;
		var entries = new CountryLabelData.Entry[countries.Length];

		for (int i = 0; i < countries.Length; i++)
		{
			Country country = countries[i];
			onProgress?.Invoke((float)i / countries.Length, country.name);
			var entry = new CountryLabelData.Entry
			{
				alpha3Code = country.alpha3Code,
				displayName = country.GetPreferredDisplayName(DisplayNameMaxLength)
			};

			Path outline = FindLargestPolygonOutline(country, out Path[] holes);
			if (outline.points == null || outline.points.Length < 3)
			{
				entry.bakeFailed = true;
				report.emptyShape++;
				entries[i] = entry;
				continue;
			}

			if (TryFindAnchor(outline, holes, out Coordinate anchor, out float angularRadius))
			{
				entry.anchor = anchor;
				entry.angularRadius = angularRadius;
				report.baked++;
			}
			else
			{
				entry.anchor = Centroid(outline);
				entry.angularRadius = 0f;
				entry.bakeFailed = true;
				report.failed++;
				report.failedNames.Add($"{country.name} ({country.alpha3Code})");
			}

			entries[i] = entry;
		}

		return entries;
	}

	// ------------------------------------------------------------- polygon selection

	/// <summary>
	/// The country's largest polygon by true spherical area, plus its holes.
	///
	/// Area, not point count: coastline detail varies wildly between countries, so a
	/// finely-traced small island can easily carry more points than a large smooth
	/// landmass. This is what keeps the USA's label in the contiguous 48 rather than on
	/// Alaska, and Norway's on the mainland rather than Svalbard.
	/// </summary>
	static Path FindLargestPolygonOutline(Country country, out Path[] holes)
	{
		holes = null;
		if (country.shape.polygons == null) { return default; }

		float bestArea = -1f;
		Path best = default;

		foreach (Polygon polygon in country.shape.polygons)
		{
			if (polygon.paths == null || polygon.paths.Length == 0) { continue; }

			Path outline = polygon.paths[0];
			if (outline.points == null || outline.points.Length < 3) { continue; }

			float area = SphericalArea(outline);
			if (area > bestArea)
			{
				bestArea = area;
				best = outline;
				holes = polygon.NumHoles > 0 ? polygon.Holes : null;
			}
		}
		return best;
	}

	/// <summary>
	/// Spherical polygon area in steradians. Longitude deltas are wrapped into
	/// [-pi, pi] so a polygon crossing the antimeridian doesn't register as spanning
	/// the whole globe.
	///
	/// Undefined for polygons with a vertex exactly at a pole, where longitude has no
	/// meaning. Such shapes fail the projection guard below anyway.
	/// </summary>
	static float SphericalArea(Path path)
	{
		Coordinate[] points = path.points;
		double total = 0;

		for (int i = 0; i < points.Length; i++)
		{
			Coordinate a = points[i];
			Coordinate b = points[(i + 1) % points.Length];

			double deltaLon = b.longitude - a.longitude;
			while (deltaLon > Mathf.PI) { deltaLon -= 2 * Mathf.PI; }
			while (deltaLon < -Mathf.PI) { deltaLon += 2 * Mathf.PI; }

			total += deltaLon * (2 + Mathf.Sin(a.latitude) + Mathf.Sin(b.latitude));
		}
		return (float)System.Math.Abs(total) * 0.5f;
	}

	// ---------------------------------------------------------------- anchor search

	static bool TryFindAnchor(Path outline, Path[] holes, out Coordinate anchor, out float angularRadius)
	{
		anchor = default;
		angularRadius = 0f;

		Vector3[] ring = ToUnitVectors(outline);

		// Gnomonic projection about the ring's mean direction. Great circles map to
		// straight lines, so polygon edges stay straight and a planar point-in-polygon
		// test is valid.
		Vector3 centre = Vector3.zero;
		foreach (Vector3 v in ring) { centre += v; }
		if (centre.sqrMagnitude < 1e-10f) { return false; }
		centre.Normalize();

		Vector3 helper = Mathf.Abs(centre.y) < 0.9f ? Vector3.up : Vector3.right;
		Vector3 e1 = Vector3.Cross(helper, centre).normalized;
		Vector3 e2 = Vector3.Cross(centre, e1);

		foreach (Vector3 v in ring)
		{
			// Spans too much of the hemisphere for gnomonic to be usable. Antarctica is
			// the expected casualty - its polygon typically runs to the pole.
			if (Vector3.Dot(v, centre) <= MinProjectionCosine) { return false; }
		}

		Vector2[] planarRing = Project(ring, centre, e1, e2);
		Vector3[][] holeRings = null;
		Vector2[][] planarHoles = null;
		if (holes != null && holes.Length > 0)
		{
			holeRings = new Vector3[holes.Length][];
			planarHoles = new Vector2[holes.Length][];
			for (int i = 0; i < holes.Length; i++)
			{
				holeRings[i] = ToUnitVectors(holes[i]);
				planarHoles[i] = Project(holeRings[i], centre, e1, e2);
			}
		}

		Vector2 min = planarRing[0], max = planarRing[0];
		foreach (Vector2 p in planarRing)
		{
			min = Vector2.Min(min, p);
			max = Vector2.Max(max, p);
		}

		Vector2 cell = new Vector2((max.x - min.x) / CoarseGridResolution,
								   (max.y - min.y) / CoarseGridResolution);

		Vector2 best = Vector2.zero;
		float bestDistance = -1f;
		bool found = false;

		for (int x = 0; x < CoarseGridResolution; x++)
		{
			for (int y = 0; y < CoarseGridResolution; y++)
			{
				Vector2 candidate = min + new Vector2((x + 0.5f) * cell.x, (y + 0.5f) * cell.y);
				if (!Evaluate(candidate, planarRing, planarHoles, ring, holeRings,
						centre, e1, e2, out float distance))
				{
					continue;
				}
				if (distance > bestDistance)
				{
					bestDistance = distance;
					best = candidate;
					found = true;
				}
			}
		}

		if (!found) { return false; }

		// Refine: halve the cell and re-test a 3x3 neighbourhood, five times over.
		// Effective resolution ends up around 2048 per axis.
		for (int pass = 0; pass < RefinementPasses; pass++)
		{
			cell *= 0.5f;
			for (int x = -1; x <= 1; x++)
			{
				for (int y = -1; y <= 1; y++)
				{
					if (x == 0 && y == 0) { continue; }

					Vector2 candidate = best + new Vector2(x * cell.x, y * cell.y);
					if (!Evaluate(candidate, planarRing, planarHoles, ring, holeRings,
							centre, e1, e2, out float distance))
					{
						continue;
					}
					if (distance > bestDistance)
					{
						bestDistance = distance;
						best = candidate;
					}
				}
			}
		}

		anchor = GeoMaths.PointToCoordinate(Unproject(best, centre, e1, e2));
		angularRadius = bestDistance;
		return true;
	}

	/// <summary>
	/// Rejects candidates outside the outline or inside a hole; otherwise returns the
	/// angular distance to the nearest border.
	///
	/// The score is measured on the sphere, not in the projected plane. Gnomonic distorts
	/// scale by 1/cos^2 radially - up to about a third for a country spanning 30 degrees -
	/// which would visibly bias the anchor toward the projection centre.
	/// </summary>
	static bool Evaluate(Vector2 candidate, Vector2[] planarRing, Vector2[][] planarHoles,
		Vector3[] ring, Vector3[][] holeRings, Vector3 centre, Vector3 e1, Vector3 e2,
		out float distance)
	{
		distance = 0f;
		if (!PointInPolygon(candidate, planarRing)) { return false; }

		if (planarHoles != null)
		{
			for (int i = 0; i < planarHoles.Length; i++)
			{
				if (PointInPolygon(candidate, planarHoles[i])) { return false; }
			}
		}

		Vector3 point = Unproject(candidate, centre, e1, e2);
		distance = MinDistanceToRing(point, ring);

		if (holeRings != null)
		{
			for (int i = 0; i < holeRings.Length; i++)
			{
				distance = Mathf.Min(distance, MinDistanceToRing(point, holeRings[i]));
			}
		}
		return true;
	}

	static float MinDistanceToRing(Vector3 point, Vector3[] ring)
	{
		float min = float.MaxValue;
		for (int i = 0; i < ring.Length; i++)
		{
			float d = DistanceToArc(point, ring[i], ring[(i + 1) % ring.Length]);
			if (d < min) { min = d; }
		}
		return min;
	}

	/// <summary>
	/// Great-circle angle from a point to the arc between a and b, in radians.
	///
	/// Note this is a true spherical distance: from (10E, 20N) to the lon=0 meridian it
	/// is asin(sin 10 * cos 20) = 9.39 degrees, not 10 - the along-a-parallel distance is
	/// not a great circle.
	/// </summary>
	static float DistanceToArc(Vector3 p, Vector3 a, Vector3 b)
	{
		Vector3 normal = Vector3.Cross(a, b);
		float normalLength = normal.magnitude;
		if (normalLength < 1e-9f) { return Vector3.Angle(p, a) * Mathf.Deg2Rad; }
		normal /= normalLength;

		float signed = Mathf.Asin(Mathf.Clamp(Vector3.Dot(p, normal), -1f, 1f));

		// Project p onto the great circle; if that foot lies between a and b, the
		// perpendicular distance is the answer, otherwise the nearest endpoint is.
		Vector3 onCircle = p - normal * Vector3.Dot(p, normal);
		if (onCircle.sqrMagnitude > 1e-18f)
		{
			onCircle.Normalize();
			if (Vector3.Dot(Vector3.Cross(a, onCircle), normal) >= 0f &&
				Vector3.Dot(Vector3.Cross(onCircle, b), normal) >= 0f)
			{
				return Mathf.Abs(signed);
			}
		}

		return Mathf.Min(Vector3.Angle(p, a), Vector3.Angle(p, b)) * Mathf.Deg2Rad;
	}

	static bool PointInPolygon(Vector2 point, Vector2[] polygon)
	{
		bool inside = false;
		int n = polygon.Length;

		for (int i = 0; i < n; i++)
		{
			Vector2 a = polygon[i];
			Vector2 b = polygon[(i + 1) % n];

			if ((a.y > point.y) != (b.y > point.y))
			{
				float crossX = a.x + (point.y - a.y) * (b.x - a.x) / (b.y - a.y);
				if (point.x < crossX) { inside = !inside; }
			}
		}
		return inside;
	}

	// -------------------------------------------------------------------- helpers

	static Vector3[] ToUnitVectors(Path path)
	{
		Coordinate[] points = path.points;
		// Source rings repeat the first point at the end; drop it so edges aren't
		// duplicated and the modulo wrap closes the ring exactly once.
		int count = points.Length;
		if (count > 1 && Approximately(points[0], points[count - 1])) { count--; }

		var result = new Vector3[count];
		for (int i = 0; i < count; i++)
		{
			result[i] = GeoMaths.CoordinateToPoint(points[i], 1f);
		}
		return result;
	}

	static bool Approximately(Coordinate a, Coordinate b)
	{
		return Mathf.Abs(a.longitude - b.longitude) < 1e-7f
			&& Mathf.Abs(a.latitude - b.latitude) < 1e-7f;
	}

	static Vector2[] Project(Vector3[] points, Vector3 centre, Vector3 e1, Vector3 e2)
	{
		var result = new Vector2[points.Length];
		for (int i = 0; i < points.Length; i++)
		{
			float d = Vector3.Dot(points[i], centre);
			result[i] = new Vector2(Vector3.Dot(points[i], e1) / d, Vector3.Dot(points[i], e2) / d);
		}
		return result;
	}

	static Vector3 Unproject(Vector2 planar, Vector3 centre, Vector3 e1, Vector3 e2)
	{
		return (centre + e1 * planar.x + e2 * planar.y).normalized;
	}

	static Coordinate Centroid(Path path)
	{
		Vector3 sum = Vector3.zero;
		foreach (Coordinate c in path.points) { sum += GeoMaths.CoordinateToPoint(c, 1f); }
		if (sum.sqrMagnitude < 1e-10f) { return path.points[0]; }
		return GeoMaths.PointToCoordinate(sum.normalized);
	}
}
