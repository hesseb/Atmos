using TerrainGeneration;
using UnityEngine;

/// <summary>
/// Swaps world scale presets live, on a key.
///
/// The point is comparison. Whether a physically based atmosphere is *practical* at strategy-game
/// framing is one of the research questions, and the honest answer turned out to be that the
/// planet size which makes sunsets work geometrically is close to unplayable. That is much easier
/// to defend when it can be demonstrated in one keypress than described in a paragraph.
///
/// **Every change is registered for undo.** AtmosphereEffect is a ScriptableObject asset, so
/// anything written to it in the editor reaches disk - the same trap RestoreScope exists for in
/// the benchmark harness. The scope is disposed when the preset changes and again on disable, so
/// leaving play mode always restores the authored values.
/// </summary>
public class WorldScaleController : MonoBehaviour
{
	[Tooltip("Presets to cycle through. The first is applied on start.")]
	public WorldScalePreset[] presets;

	public AtmosphereEffect atmosphere;
	public TestbedCamera testbedCamera;

	[Header("Planet scaling")]
	[Tooltip("Root of everything at planet scale - terrain, outlines, ocean, city lights. The " +
		"Solar System is deliberately NOT under it: the sun and stars must not scale.")]
	public Transform worldRoot;

	[Tooltip("Read by the camera, the picker, the labels and the highlight for surface radius. " +
		"A ScriptableObject, so changes to it are undone through the scope like the rest.")]
	public TerrainHeightSettings heightSettings;

	public SimpleLodSystem lodSystem;
	public Camera renderCamera;

	[Tooltip("Cycles to the next preset. F5 and F6 belong to the benchmark HUD.")]
	public KeyCode cycleKey = KeyCode.F7;

	[Tooltip("Apply the first preset on start. Off leaves the scene exactly as authored, which " +
		"is what a measurement run wants - a benchmark should not silently inherit a scale.")]
	public bool applyOnStart = true;

	int current = -1;

	/// <summary>Undo for the applied preset. Null when the scene is as authored.</summary>
	RestoreScope scope;

	/// <summary>The planet scale currently applied, or 1 when the scene is as authored.</summary>
	float ScaleInEffect => Current != null ? Current.planetScale : 1f;

	public WorldScalePreset Current =>
		presets != null && current >= 0 && current < presets.Length ? presets[current] : null;

	void Start()
	{
		Resolve();
		if (applyOnStart && presets != null && presets.Length > 0) { Apply(0); }
	}

	/// <summary>
	/// Fills in whatever was left unassigned, the same way BenchmarkSceneRefs does.
	///
	/// The atmosphere is a ScriptableObject asset rather than a scene object, so it cannot be
	/// found directly - it is reached through the PostProcessingManager's effect list, which is
	/// the only thing in the scene that references it.
	/// </summary>
	void Resolve()
	{
		if (testbedCamera == null) { testbedCamera = FindFirstObjectByType<TestbedCamera>(); }
		if (lodSystem == null) { lodSystem = FindFirstObjectByType<SimpleLodSystem>(); }
		if (renderCamera == null) { renderCamera = testbedCamera != null ? testbedCamera.GetComponent<Camera>() : Camera.main; }

		// The camera already holds it, and it must be the SAME asset instance or the camera would
		// keep reading an unscaled radius while everything else scaled.
		if (heightSettings == null && testbedCamera != null) { heightSettings = testbedCamera.heightSettings; }

		if (worldRoot == null)
		{
			GameObject world = GameObject.Find("World");
			if (world != null) { worldRoot = world.transform; }
		}

		if (atmosphere == null)
		{
			PostProcessingManager postProcessing = FindFirstObjectByType<PostProcessingManager>();
			if (postProcessing != null && postProcessing.effects != null)
			{
				foreach (PostProcessingEffect effect in postProcessing.effects)
				{
					if (effect is AtmosphereEffect found) { atmosphere = found; break; }
				}
			}
		}

		if (atmosphere == null) { Debug.LogWarning("[WorldScale] no AtmosphereEffect found.", this); }
	}

	void OnDisable()
	{
		// Restores the asset, not just the scene. Without this a play session would leave
		// whichever preset was last selected written into Atmosphere.asset.
		scope?.Dispose();
		scope = null;
		current = -1;
	}

	void Update()
	{
		if (Input.GetKeyDown(cycleKey)) { Cycle(); }
	}

	public void Cycle()
	{
		if (presets == null || presets.Length == 0)
		{
			Debug.LogWarning("[WorldScale] no presets assigned.", this);
			return;
		}
		Apply((current + 1) % presets.Length);
	}

	public void Apply(int index)
	{
		if (presets == null || index < 0 || index >= presets.Length) { return; }

		WorldScalePreset preset = presets[index];
		if (preset == null) { return; }

		// Unwind the previous preset first, so each scope holds the *authored* values rather
		// than the previous preset's - otherwise cycling twice would make the restore a no-op
		// and the authored state would be lost.
		scope?.Dispose();
		scope = new RestoreScope();
		current = index;

		if (atmosphere != null)
		{
			scope.Set(() => atmosphere.atmosphereThickness, v => atmosphere.atmosphereThickness = v, preset.atmosphereThickness);
			scope.Set(() => atmosphere.densityMultiplier, v => atmosphere.densityMultiplier = v, preset.densityMultiplier);
			scope.Set(() => atmosphere.intensity, v => atmosphere.intensity = v, preset.intensity);
			scope.Set(() => atmosphere.contrast, v => atmosphere.contrast = v, preset.contrast);
			scope.Set(() => atmosphere.whitePoint, v => atmosphere.whitePoint = v, preset.whitePoint);

			// The LUTs describe the old atmosphere until this is called, and nothing else would
			// call it - OnValidate only fires for inspector edits.
			atmosphere.MarkSettingsDirty();
			scope.Add(() => atmosphere.MarkSettingsDirty());
		}

		// Planet geometry. Everything below derives from the same scale, so they cannot drift.
		if (!Mathf.Approximately(preset.planetScale, 1f) || worldRoot != null)
		{
			float k = Mathf.Max(1e-3f, preset.planetScale);

			if (worldRoot != null)
			{
				scope.Set(() => worldRoot.localScale, v => worldRoot.localScale = v, Vector3.one * k);
			}

			// The authored radius, read before the scope overwrites it - Set captures the current
			// value as the undo, and cycling disposes first, so this is always the authored one.
			if (heightSettings != null)
			{
				float baseRadius = heightSettings.worldRadius;
				scope.Set(() => heightSettings.worldRadius, v => heightSettings.worldRadius = v, baseRadius * k);
				if (atmosphere != null)
				{
					scope.Set(() => atmosphere.bodyRadius, v => atmosphere.bodyRadius = v, baseRadius * k);
				}
			}

			// LOD picks high-res by world-space distance, so an unscaled threshold would put the
			// whole planet in low res at 4x.
			if (lodSystem != null)
			{
				scope.Set(() => lodSystem.highResDistanceThreshold,
					v => lodSystem.highResDistanceThreshold = v, lodSystem.highResDistanceThreshold * k);
			}

			// Far clip is 600 against a 400-unit camera radius today - barely enough to reach the
			// planet's far side, and nowhere near it once the planet grows.
			if (renderCamera != null)
			{
				scope.Set(() => renderCamera.farClipPlane, v => renderCamera.farClipPlane = v, renderCamera.farClipPlane * k);
			}

			if (testbedCamera != null)
			{
				scope.Set(() => testbedCamera.maxAltitude, v => testbedCamera.maxAltitude = v, testbedCamera.maxAltitude * k);
			}
		}

		if (testbedCamera != null)
		{
			scope.Set(() => testbedCamera.referenceAltitude, v => testbedCamera.referenceAltitude = v, preset.referenceAltitude);
			scope.Set(() => testbedCamera.panSpeed, v => testbedCamera.panSpeed = v, preset.panSpeed);
			scope.Set(() => testbedCamera.flySpeed, v => testbedCamera.flySpeed = v, preset.flySpeed);
			// Orbit mode re-applies the pose every frame, so writing the field is enough to move
			// the camera. Free-fly derives altitude from the transform instead and will not
			// jump - which is the right behaviour, since a preset should not teleport a camera
			// the user is flying.
			scope.Set(() => testbedCamera.altitude, v => testbedCamera.altitude = v, preset.altitude);
		}

		Debug.Log($"[WorldScale] {preset.id}: {Summary(preset)}", this);
	}

	/// <summary>One line for the log and the HUD - the numbers the presets actually differ in.</summary>
	public string Summary(WorldScalePreset preset)
	{
		if (preset == null) { return "scene as authored"; }
		if (atmosphere == null) { return preset.id; }

		// From the authored radius, not the live one, which the scope may already have scaled.
		float baseRadius = heightSettings != null ? heightSettings.worldRadius / Mathf.Max(1e-3f, ScaleInEffect) : atmosphere.bodyRadius;
		float km = preset.PlanetRadiusKm(baseRadius);
		float airMass = preset.HorizonAirMass(baseRadius, atmosphere.rayleighDensityAvg);

		return $"planet x{preset.planetScale:F1} = {km:F0} km, air mass {airMass:F1} (Earth 35.4), "
			+ $"density x{preset.densityMultiplier:F2}";
	}
}
