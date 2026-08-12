using UnityEngine;
using SolarSystem;

/// <summary>
/// Runtime time-of-day controls for the testbed: speed up / slow down / pause the day
/// cycle, and jump the sun to exact positions relative to wherever the camera is.
///
/// The presets are solved analytically by <see cref="SolarTime"/>, so "sunset" means the
/// sun is at exactly 0 degrees elevation and descending at the observer's location - not
/// an approximation, and repeatable to the same dayT every time. That matters for the
/// thesis: twilight is where ozone absorption and the Mie forward lobe do most of their
/// visible work, so those comparisons need to be reproducible across renderers.
///
/// MEASUREMENT NOTE: hide the overlay before timing anything. IMGUI
/// allocates and costs frame time.
/// </summary>
public class TimeController : MonoBehaviour
{
	[Header("References")]
	public SolarSystemManager solarSystem;
	// Defaults to the main camera. Presets are relative to this transform's position.
	public Transform observer;

	[Header("Speed")]
	public float[] speedSteps = { 0.1f, 0.25f, 1f, 4f, 16f, 64f, 256f };
	public int speedIndex = 2;
	// J and K rather than comma and period: they sit under the right hand next to the movement
	// keys, and the punctuation pair is easy to hit by accident while typing in the console.
	public KeyCode slowerKey = KeyCode.J;
	public KeyCode fasterKey = KeyCode.K;
	public KeyCode pauseKey = KeyCode.P;

	[Header("Sun presets")]
	public KeyCode sunriseKey = KeyCode.Alpha1;
	public KeyCode noonKey = KeyCode.Alpha2;
	public KeyCode sunsetKey = KeyCode.Alpha3;
	public KeyCode midnightKey = KeyCode.Alpha4;
	public KeyCode goldenKey = KeyCode.Alpha5;
	// Elevation used by the golden-hour preset. Low sun, still above the horizon - the
	// condition where aerial perspective and Mie scattering are most visible.
	public float goldenElevationDegrees = 5f;

	public bool inputEnabled = true;

	EarthOrbit Earth => solarSystem != null ? solarSystem.earth : null;

	Vector3 ObserverUp
	{
		get
		{
			Transform t = observer != null ? observer : (Camera.main != null ? Camera.main.transform : null);
			if (t == null || t.position.sqrMagnitude < 1e-8f) { return Vector3.up; }
			return t.position.normalized;
		}
	}

	void OnEnable()
	{
		if (solarSystem == null) { solarSystem = FindFirstObjectByType<SolarSystemManager>(); }
		if (observer == null && Camera.main != null) { observer = Camera.main.transform; }
		ApplySpeed();
	}

	void Update()
	{
		if (!inputEnabled) { return; }


		if (Input.GetKeyDown(pauseKey) && solarSystem != null)
		{
			// Nothing is echoed anywhere: the overlay shows state, and "paused" is already in it.
			solarSystem.animate = !solarSystem.animate;
		}

		if (Input.GetKeyDown(slowerKey)) { StepSpeed(-1); }
		if (Input.GetKeyDown(fasterKey)) { StepSpeed(1); }

		if (Input.GetKeyDown(sunriseKey)) { SetSunElevation(0f, rising: true, "sunrise"); }
		if (Input.GetKeyDown(sunsetKey)) { SetSunElevation(0f, rising: false, "sunset"); }
		if (Input.GetKeyDown(goldenKey)) { SetSunElevation(goldenElevationDegrees, rising: false, "golden hour"); }
		if (Input.GetKeyDown(noonKey)) { SetSunToExtreme(highest: true); }
		if (Input.GetKeyDown(midnightKey)) { SetSunToExtreme(highest: false); }
	}

	// ------------------------------------------------------------------ speed

	void StepSpeed(int direction)
	{
		if (speedSteps == null || speedSteps.Length == 0) { return; }
		speedIndex = Mathf.Clamp(speedIndex + direction, 0, speedSteps.Length - 1);
		ApplySpeed();
	}

	void ApplySpeed()
	{
		if (solarSystem != null) { solarSystem.timeMultiplier = CurrentSpeed; }
	}

	public float CurrentSpeed
	{
		get
		{
			if (speedSteps == null || speedSteps.Length == 0) { return 1f; }
			return speedSteps[Mathf.Clamp(speedIndex, 0, speedSteps.Length - 1)];
		}
	}

	public void SetSpeed(float multiplier)
	{
		if (solarSystem != null) { solarSystem.timeMultiplier = multiplier; }
	}

	// ------------------------------------------------------------- sun presets

	/// <summary>
	/// Puts the sun at an exact elevation above the observer's horizon. Returns false if
	/// that elevation is unreachable here (polar day/night) or the observer is at a pole,
	/// in which case the time is left unchanged.
	/// </summary>
	public bool SetSunElevation(float elevationDegrees, bool rising)
	{
		if (solarSystem == null || Earth == null) { return false; }

		if (!SolarTime.TrySolveDayT(Earth, ObserverUp, solarSystem.yearT,
				elevationDegrees, rising, out float dayT))
		{
			return false;
		}

		solarSystem.SetTimes(dayT, solarSystem.monthT, solarSystem.yearT);
		return true;
	}

	void SetSunElevation(float elevationDegrees, bool rising, string label)
	{
		if (!SetSunElevation(elevationDegrees, rising))
		{
			// Genuinely informative rather than an error: at high latitude the sun may never reach
			// this elevation at this time of year. To the console, because the overlay shows STATE
			// now rather than echoing actions - and a preset that did nothing has no state to show.
			Debug.LogWarning($"[Time] {label} unreachable at this latitude/season.", this);
		}
	}

	/// <summary>Local solar noon (sun highest) or midnight (sun lowest).</summary>
	public void SetSunToExtreme(bool highest)
	{
		if (solarSystem == null || Earth == null) { return; }

		float dayT = SolarTime.SolveDayTForExtreme(Earth, ObserverUp, solarSystem.yearT, highest);
		solarSystem.SetTimes(dayT, solarSystem.monthT, solarSystem.yearT);
	}

	public float CurrentSunElevation
	{
		get
		{
			if (solarSystem == null || Earth == null) { return 0f; }
			return SolarTime.ElevationDegrees(Earth, ObserverUp, solarSystem.dayT, solarSystem.yearT);
		}
	}

	public float CurrentLocalSolarHours
	{
		get
		{
			if (solarSystem == null || Earth == null) { return 12f; }
			return SolarTime.LocalSolarHours(Earth, ObserverUp, solarSystem.dayT, solarSystem.yearT);
		}
	}

	// ---------------------------------------------------------------- overlay

// The overlay lives in BenchmarkHud now - one panel, one hide key, rather than two stacked in
	// opposite corners each with its own toggle. This class still owns the time KEYS and the state
	// behind them; only the drawing moved.

	// Edit-mode access, for composing a shot without entering play mode.
	// SolarSystemManager is [ExecuteInEditMode], so the orbit updates immediately.
	[ContextMenu("Sun/Sunrise")] void CtxSunrise() { EnsureRefs(); SetSunElevation(0f, true, "sunrise"); }
	[ContextMenu("Sun/Noon")] void CtxNoon() { EnsureRefs(); SetSunToExtreme(true); }
	[ContextMenu("Sun/Sunset")] void CtxSunset() { EnsureRefs(); SetSunElevation(0f, false, "sunset"); }
	[ContextMenu("Sun/Midnight")] void CtxMidnight() { EnsureRefs(); SetSunToExtreme(false); }
	[ContextMenu("Sun/Golden Hour")] void CtxGolden() { EnsureRefs(); SetSunElevation(goldenElevationDegrees, false, "golden"); }

	[ContextMenu("Sun/Log Current Position")]
	void CtxLog()
	{
		EnsureRefs();
		Debug.Log($"solar {SolarTime.FormatHours(CurrentLocalSolarHours)}, " +
			$"sun elevation {CurrentSunElevation:0.00}°, dayT {solarSystem.dayT:0.000000}, " +
			$"yearT {solarSystem.yearT:0.000000}", this);
	}

	void EnsureRefs()
	{
		if (solarSystem == null) { solarSystem = FindFirstObjectByType<SolarSystemManager>(); }
		if (observer == null && Camera.main != null) { observer = Camera.main.transform; }
	}

	void OnValidate()
	{
		if (speedSteps != null && speedSteps.Length > 0)
		{
			speedIndex = Mathf.Clamp(speedIndex, 0, speedSteps.Length - 1);
		}
		if (Application.isPlaying) { ApplySpeed(); }
	}
}
