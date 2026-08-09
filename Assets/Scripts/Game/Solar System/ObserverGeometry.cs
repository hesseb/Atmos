using UnityEngine;

/// <summary>
/// Where the sun is relative to an observer standing on the planet.
///
/// Shared by the baseline sky and the baseline aerial perspective so the two cannot disagree
/// about what time of day it is. They are separately authored - a gradient texture and a
/// colour ramp - and if each derived sun elevation its own way, a small difference would show
/// up as haze that does not match the horizon it fades into, which reads as a rendering bug
/// rather than as the parameter drift it actually is.
/// </summary>
public static class ObserverGeometry
{
	/// <summary>
	/// The sun's height above the observer's local horizon, remapped to 0..1 across the full
	/// -90..+90 degrees. 0.5 is exactly on the horizon.
	///
	/// This is the axis both the sky gradient LUT and the haze ramp are indexed by, so its
	/// definition is load-bearing: changing the mapping invalidates every authored texture.
	/// </summary>
	public static float SunElevation01(Vector3 observerPosition, Vector3 planetCentre, Vector3 dirToSun)
	{
		Vector3 up = observerPosition - planetCentre;
		if (up.sqrMagnitude < 1e-8f || dirToSun.sqrMagnitude < 1e-8f) { return 0.5f; }

		return Mathf.Clamp01(Vector3.Dot(dirToSun, up.normalized) * 0.5f + 0.5f);
	}

	/// <summary>
	/// Centre of the planet. Not the origin - the earth orbits.
	///
	/// Caches into the supplied field, because a ScriptableObject cannot serialize a scene
	/// reference and the callers that need this most are assets.
	/// </summary>
	public static Vector3 PlanetCentre(ref Transform cached)
	{
		if (cached != null) { return cached.position; }

		SolarSystemManager solarSystem = Object.FindFirstObjectByType<SolarSystemManager>();
		if (solarSystem != null && solarSystem.earth != null)
		{
			cached = solarSystem.earth.transform;
			return cached.position;
		}
		return Vector3.zero;
	}

	/// <summary>Direction from the observer toward the sun. Zero if there is no sun.</summary>
	public static Vector3 DirectionToSun(ref Light cached)
	{
		if (cached == null)
		{
			// Same lookup AtmosphereEffect uses, so both renderers follow the same light.
			GameObject sunObject = GameObject.FindGameObjectWithTag("Sun");
			cached = sunObject != null ? sunObject.GetComponent<Light>() : null;
		}

		return cached != null ? -cached.transform.forward : Vector3.zero;
	}
}
