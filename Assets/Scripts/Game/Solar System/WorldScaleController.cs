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

	[Tooltip("Owns the camera far clip, per-layer cull distances and shadow distances - all in " +
		"world units, so all of them have to move with the planet.")]
	public RenderSettingsController renderSettings;

	[Tooltip("Labels pose from the live globe radius; this is only nudged in case it is built late.")]
	public CountryLabelSystem labelSystem;

	[Tooltip("Positions live in a compute buffer drawn indirectly, so they need rewriting.")]
	public CityLights cityLights;

	[Tooltip("Holds one terrain copy per planet scale, so relief stays at its authored " +
		"world-unit height instead of scaling with the globe.")]
	public LodMeshLoader terrainLoader;

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
		if (terrainLoader == null) { terrainLoader = FindFirstObjectByType<LodMeshLoader>(); }
		// Three separate statements, not a conditional. The Camera lives on its own GameObject,
		// so GetComponent returns null - and written as `testbedCamera != null ? GetComponent :
		// Camera.main`, the fallback never ran, renderCamera stayed null, and the far clip was
		// never scaled. Terrain then vanished past 600 units at every planet scale.
		if (renderSettings == null) { renderSettings = FindFirstObjectByType<RenderSettingsController>(); }
		if (labelSystem == null) { labelSystem = FindFirstObjectByType<CountryLabelSystem>(); }
		if (cityLights == null) { cityLights = FindFirstObjectByType<CityLights>(); }

		// TestbedCamera holds the rendering camera explicitly; that is more reliable than
		// GetComponent, which fails here because the Camera lives on its own GameObject.
		if (renderCamera == null && testbedCamera != null) { renderCamera = testbedCamera.cam; }
		if (renderCamera == null) { renderCamera = Camera.main; }
		if (renderCamera == null) { renderCamera = FindFirstObjectByType<Camera>(); }

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
		// Captured before `current` moves on, so the camera's height can be carried across as a
		// fraction of the planet rather than as a distance.
		float previousScale = ScaleInEffect;

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

		// Planet geometry. Everything derives from the same scale, so nothing can drift.
		//
		// At method scope because the camera block below needs them too. baseRadius is the
		// authored radius: the scope is disposed before this point, so worldRadius has already
		// been restored and reads as authored rather than as a previously scaled value.
		float k = Mathf.Max(1e-3f, preset.planetScale);
		float baseRadius = heightSettings != null ? heightSettings.worldRadius : 150f;

		if (!Mathf.Approximately(preset.planetScale, 1f) || worldRoot != null)
		{

			// The World transform is deliberately NOT scaled. The planet radius is baked into the
			// mesh vertices instead - see PlanetRelief.Correct. Terrain, outlines and ocean are
			// statically batched, and static batching bakes renderer bounds in world space at
			// combine time, so scaling the root left every chunk carrying bounds from its
			// unscaled position and Unity frustum-culled chunks that were plainly on screen.
			// That is the panels-of-the-planet-vanishing artefact, and it got worse with scale.
			//
			// Nothing else under World needs the transform: the remaining children are logic
			// (lookup, interaction, LOD, height processor) or generated from worldRadius, which
			// is set below.
			if (worldRoot != null && worldRoot.localScale != Vector3.one)
			{
				scope.Set(() => worldRoot.localScale, v => worldRoot.localScale = v, Vector3.one);
			}

			// worldRadius drives the camera's surface radius, the picker, the labels, the country
			// highlight and the city lights, so it has to agree with the baked meshes.
			if (heightSettings != null)
			{
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

			// Culling distances. These are the whole story behind terrain vanishing on a scaled
			// planet, and none of them are the far clip alone: RenderSettingsController also sets
			// layerCullDistances, which culls per layer by distance regardless of the frustum,
			// and it applies all of it in Awake so anything set afterwards is overwritten.
			//
			// Terrain sits on a layer capped at 400 units. The visible surface reaches the horizon
			// at sqrt(r^2 - R^2), which is 316 units from the highest reachable altitude on the x1
			// planet - just inside - but 492 at x16 from only 50 up. Hence a planet that never
			// culled at x1, culled after some zoom at x4, and culled almost immediately at x16.
			if (renderSettings != null)
			{
				scope.Set(() => renderSettings.maxCameraCullDst, v => renderSettings.maxCameraCullDst = v,
					renderSettings.maxCameraCullDst * k);
				scope.Set(() => renderSettings.maxLightShadowCullDst, v => renderSettings.maxLightShadowCullDst = v,
					renderSettings.maxLightShadowCullDst * k);
				scope.Set(() => renderSettings.shadowDrawDistance, v => renderSettings.shadowDrawDistance = v,
					renderSettings.shadowDrawDistance * k);

				// Per-layer overrides are a struct array, so it is replaced wholesale rather than
				// mutated in place - a copy of the authored array is what the scope restores.
				RenderSettingsController.LayerOverride[] authored = renderSettings.layerOverrides;
				if (authored != null)
				{
					var scaled = new RenderSettingsController.LayerOverride[authored.Length];
					for (int i = 0; i < authored.Length; i++)
					{
						scaled[i] = authored[i];
						scaled[i].cameraCullDst *= k;
						scaled[i].shadowCullDst *= k;
					}
					scope.Set(() => renderSettings.layerOverrides, v => renderSettings.layerOverrides = v, scaled);
				}

				renderSettings.ApplySettings();
				scope.Add(() => renderSettings.ApplySettings());
			}

			// Labels pose from the live radius and notice a change themselves, so they only need
			// a nudge in case this ran before their own Start built them.
			if (labelSystem != null) { labelSystem.SetGlobeRadius(); }

			// City light positions live in a ComputeBuffer drawn indirectly, so no transform moves
			// them and the buffer has to be rewritten. CityLights also applies this itself when it
			// initialises, since it is created by a loading task that may run after this.
			if (cityLights != null) { cityLights.SetPlanetScale(baseRadius, k); }

			if (testbedCamera != null)
			{
				scope.Set(() => testbedCamera.maxAltitude, v => testbedCamera.maxAltitude = v, testbedCamera.maxAltitude * k);
			}

			// The terrain copy whose relief was pre-divided by this scale, so the x k transform
			// leaves mountains at their authored height rather than k times it.
			// Every loader that holds per-scale copies, not just the terrain. Country outlines sit
			// at a small offset above the surface, so if their copy is not switched with the
			// terrain's the borders end up floating in the sky at k times their offset.
			foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
			{
				if (behaviour is IPlanetScaleSelectable selectable && !selectable.SelectScale(k))
				{
					Debug.LogWarning($"[WorldScale] {behaviour.name} has no copy for scale {k}; its " +
						"relief will scale with the planet. Add it to that loader's planetScales.", behaviour);
				}
			}
		}

		if (testbedCamera != null)
		{
			// Panning is an ARC rate, so the surface speed it produces is arc * R and grows in
			// direct proportion to the planet.
			//
			// What it should be measured against is how much ground is on screen - and at the
			// altitudes actually used, that is set by the field of view, not by the planet. At
			// altitude 10 with a 60 degree FOV the view spans about 11.5 units of ground whether
			// the horizon is 55 units away or 219. So the visible width does NOT grow with the
			// planet, and the apparent speed goes as R itself.
			//
			// An earlier version divided by sqrt(planetScale), reasoning from the horizon
			// distance. That is the right measure only when zoomed far enough out for the planet
			// to limit the view, which is not where the camera spends its time - and it left
			// panning four times too fast at x16.
			//
			// Flying needs no correction at all: it is a world-unit rate scaled by altitude, and
			// the visible width is also proportional to altitude, so the two already cancel and
			// nothing about the planet enters.
			scope.Set(() => testbedCamera.referenceAltitude, v => testbedCamera.referenceAltitude = v, preset.referenceAltitude);
			scope.Set(() => testbedCamera.panSpeed, v => testbedCamera.panSpeed = v,
				preset.panSpeed / Mathf.Max(1e-3f, preset.planetScale));
			scope.Set(() => testbedCamera.flySpeed, v => testbedCamera.flySpeed = v, preset.flySpeed);

			// modeSwitchAltitude is deliberately absent: it is a fraction of the planet radius, so
			// it follows the scale on its own and there is nothing here to keep in step.
			// Keep the FRAMING across a scale change rather than a distance.
			//
			// Forcing the preset's altitude meant landing 10 units above a 2400-unit globe at x16 -
			// on top of the surface, with nothing recognisable in view. Holding the altitude as a
			// fraction of the radius instead makes the swap a pure change of planet size: the same
			// amount of world stays in frame, the same curvature, and what visibly differs is the
			// atmosphere, which is the whole point of being able to switch.
			//
			// Moved rather than assigned, and last, after the new surface radius is in place -
			// writing the field alone only works in Orbit mode, since free-fly treats the transform
			// as authoritative and merely derives altitude from it.
			float previousRadius = baseRadius * Mathf.Max(1e-3f, previousScale);
			float previousAltitude = testbedCamera.transform.position.magnitude - previousRadius;
			float altitudeFraction = previousAltitude / previousRadius;

			// Never below the preset's working altitude, which is the height its atmosphere and
			// camera speeds are tuned around.
			float targetAltitude = Mathf.Max(preset.altitude, altitudeFraction * baseRadius * k);
			testbedCamera.SetAltitudeAboveSurface(targetAltitude);
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
