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
/// MEASUREMENT NOTE: turn <see cref="showOverlay"/> off before timing anything. IMGUI
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
	public KeyCode slowerKey = KeyCode.Comma;
	public KeyCode fasterKey = KeyCode.Period;
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

	[Header("Overlay")]
	public bool showOverlay = true;
	public KeyCode toggleOverlayKey = KeyCode.F1;
	public int overlayFontSize = 18;
	public Color overlayTextColour = Color.white;
	public Color overlayBackgroundColour = new Color(0f, 0f, 0f, 0.66f);

	public bool inputEnabled = true;

	string lastAction = "";
	GUIStyle overlayStyle;
	Texture2D overlayBackground;
	Color appliedBackgroundColour;

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
		if (solarSystem == null) { solarSystem = FindObjectOfType<SolarSystemManager>(); }
		if (observer == null && Camera.main != null) { observer = Camera.main.transform; }
		ApplySpeed();
	}

	void Update()
	{
		if (!inputEnabled) { return; }

		if (Input.GetKeyDown(toggleOverlayKey)) { showOverlay = !showOverlay; }

		if (Input.GetKeyDown(pauseKey) && solarSystem != null)
		{
			solarSystem.animate = !solarSystem.animate;
			lastAction = solarSystem.animate ? "resumed" : "paused";
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
		lastAction = $"speed {CurrentSpeed:0.##}x";
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
		if (SetSunElevation(elevationDegrees, rising))
		{
			lastAction = label;
		}
		else
		{
			// Genuinely informative rather than an error: at high latitude the sun may
			// never reach this elevation at this time of year.
			lastAction = $"{label} unreachable at this latitude/season";
		}
	}

	/// <summary>Local solar noon (sun highest) or midnight (sun lowest).</summary>
	public void SetSunToExtreme(bool highest)
	{
		if (solarSystem == null || Earth == null) { return; }

		float dayT = SolarTime.SolveDayTForExtreme(Earth, ObserverUp, solarSystem.yearT, highest);
		solarSystem.SetTimes(dayT, solarSystem.monthT, solarSystem.yearT);
		lastAction = highest ? "noon" : "midnight";
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

	void OnGUI()
	{
		if (!showOverlay || solarSystem == null || Earth == null) { return; }

		// Built from a bare GUIStyle rather than GUI.skin.label: the editor skin's label
		// is dark text intended for a light background, and a style copied from it can
		// also lose an overridden colour across a domain reload. Colour and size are
		// re-applied every frame so the overlay never silently reverts to black.
		if (overlayStyle == null)
		{
			overlayStyle = new GUIStyle { richText = false, wordWrap = false };
			overlayStyle.padding = new RectOffset(12, 12, 10, 10);
		}
		overlayStyle.fontSize = Mathf.Max(1, overlayFontSize);
		overlayStyle.normal.textColor = overlayTextColour;

		// Only rebuilt when the colour actually changes - OnGUI runs several times a frame.
		if (overlayBackground == null || appliedBackgroundColour != overlayBackgroundColour)
		{
			if (overlayBackground == null)
			{
				overlayBackground = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
			}
			overlayBackground.SetPixel(0, 0, overlayBackgroundColour);
			overlayBackground.Apply();
			appliedBackgroundColour = overlayBackgroundColour;
		}

		string state = solarSystem.animate ? $"{CurrentSpeed:0.##}x" : "paused";
		string text =
			$"solar {SolarTime.FormatHours(CurrentLocalSolarHours)}   sun {CurrentSunElevation:+0.0;-0.0}°   {state}\n" +
			$"dayT {solarSystem.dayT:0.0000}   yearT {solarSystem.yearT:0.0000}\n" +
			$"1 sunrise  2 noon  3 sunset  4 midnight  5 golden({goldenElevationDegrees:0.#}°)\n" +
			$", . speed   P pause   F1 hide" +
			(string.IsNullOrEmpty(lastAction) ? "" : $"\n{lastAction}");

		// Size to the content so the panel tracks the font size and the optional last
		// action line, rather than clipping at a hardcoded rect.
		GUIContent content = new GUIContent(text);
		Vector2 size = overlayStyle.CalcSize(content);
		Rect rect = new Rect(10f, 10f, size.x, size.y);

		GUI.DrawTexture(rect, overlayBackground);
		GUI.Label(rect, content, overlayStyle);
	}

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
		if (solarSystem == null) { solarSystem = FindObjectOfType<SolarSystemManager>(); }
		if (observer == null && Camera.main != null) { observer = Camera.main.transform; }
	}

	void OnDestroy()
	{
		if (overlayBackground != null)
		{
			if (Application.isPlaying) { Destroy(overlayBackground); }
			else { DestroyImmediate(overlayBackground); }
			overlayBackground = null;
		}
	}

	void OnValidate()
	{
		if (speedSteps != null && speedSteps.Length > 0)
		{
			speedIndex = Mathf.Clamp(speedIndex, 0, speedSteps.Length - 1);
		}
		overlayFontSize = Mathf.Clamp(overlayFontSize, 8, 48);
		if (Application.isPlaying) { ApplySpeed(); }
	}
}
