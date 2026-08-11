using System.Collections.Generic;
using UnityEngine;
using SolarSystem;

/// <summary>
/// Scene objects the harness drives. Auto-resolved where possible so a benchmark scene
/// needs no wiring, but overridable in the inspector.
/// </summary>
[System.Serializable]
public class BenchmarkSceneRefs
{
	public Camera camera;
	public TestbedCamera testbedCamera;
	public SolarSystemManager solarSystem;
	public TimeController timeController;
	public SimpleLodSystem lodSystem;
	public PostProcessingManager postProcessing;
	// The GameObject carrying GlobePicker + CountryHighlight + CountryLabelSystem.
	public GameObject countryInteraction;
	// Owns the sky command buffers and reports which sky is actually attached.
	public RenderingManager renderingManager;
	public BaselineSkyRenderer baselineSky;

	public EarthOrbit Earth => solarSystem != null ? solarSystem.earth : null;

	public bool Resolve(out string error)
	{
		if (camera == null) { camera = Camera.main; }
		if (testbedCamera == null) { testbedCamera = Object.FindFirstObjectByType<TestbedCamera>(); }
		if (solarSystem == null) { solarSystem = Object.FindFirstObjectByType<SolarSystemManager>(); }
		if (timeController == null) { timeController = Object.FindFirstObjectByType<TimeController>(); }
		if (lodSystem == null) { lodSystem = Object.FindFirstObjectByType<SimpleLodSystem>(); }
		if (postProcessing == null) { postProcessing = Object.FindFirstObjectByType<PostProcessingManager>(); }

		if (countryInteraction == null)
		{
			GlobePicker picker = Object.FindFirstObjectByType<GlobePicker>(FindObjectsInactive.Include);
			if (picker != null) { countryInteraction = picker.gameObject; }
		}

		if (renderingManager == null)
		{
			renderingManager = Object.FindFirstObjectByType<RenderingManager>();
		}
		if (baselineSky == null)
		{
			// FindObjectsInactive.Include, because a profile that enables the baseline needs a
			// handle to it while it is disabled - which is its resting state whenever the
			// physically based renderer is the one being measured.
			baselineSky = Object.FindFirstObjectByType<BaselineSkyRenderer>(FindObjectsInactive.Include);
		}

		var missing = new List<string>();
		if (camera == null) { missing.Add(nameof(camera)); }
		if (testbedCamera == null) { missing.Add(nameof(testbedCamera)); }
		if (solarSystem == null) { missing.Add(nameof(solarSystem)); }
		if (solarSystem != null && solarSystem.earth == null) { missing.Add("solarSystem.earth"); }
		if (lodSystem == null) { missing.Add(nameof(lodSystem)); }

		error = missing.Count == 0 ? null : "missing scene references: " + string.Join(", ", missing);
		return missing.Count == 0;
	}
}

/// <summary>
/// Pins everything that would otherwise perturb or de-randomise a measurement, and puts it
/// all back afterwards.
///
/// The list is not arbitrary - every entry is something measured or reasoned about:
/// animation that integrates delta time, UI that allocates per frame, LOD selection that
/// depends on previous frames, and input that depends on where the mouse happens to be.
/// </summary>
public static class BenchmarkEnvironment
{
	public struct Settings
	{
		public float simulatedFps;
		public Vector2Int targetResolution;
		public bool pinResolution;
		public int lodFramesPerUpdate;
	}

	public static Settings Defaults => new Settings
	{
		simulatedFps = 60f,
		targetResolution = new Vector2Int(1920, 1080),
		pinResolution = true,
		lodFramesPerUpdate = 1
	};

	/// <summary>
	/// Applies the pinned environment. Dispose the returned scope to restore everything.
	/// Warnings describe conditions that make a run less trustworthy but do not stop it.
	/// </summary>
	public static RestoreScope Pin(BenchmarkSceneRefs refs, Settings settings, List<string> warnings)
	{
		var scope = new RestoreScope();

		// ---- time -------------------------------------------------------------
		// captureDeltaTime fixes Time.deltaTime (and therefore Time.time, and the
		// _Time the ocean shader animates from) without throttling - measured, see NOTES.
		// Time.unscaledDeltaTime is NOT pinned by it and still reports real elapsed time.
		scope.Set(() => Time.captureDeltaTime, v => Time.captureDeltaTime = v,
			1f / Mathf.Max(1f, settings.simulatedFps));
		scope.Set(() => Application.targetFrameRate, v => Application.targetFrameRate = v, -1);
		scope.Set(() => QualitySettings.vSyncCount, v => QualitySettings.vSyncCount = v, 0);
		scope.Set(() => Application.runInBackground, v => Application.runInBackground = v, true);

		if (QualitySettings.vSyncCount != 0)
		{
			warnings.Add("VSYNC_ON");
		}

		// ---- resolution -------------------------------------------------------
		// Screen.SetResolution is a no-op in the editor, so the caller must compare
		// requested against actual rather than assume this took.
		if (settings.pinResolution && !Application.isEditor)
		{
			Screen.SetResolution(settings.targetResolution.x, settings.targetResolution.y,
				FullScreenMode.Windowed);
		}

		// ---- input and overlays ----------------------------------------------
		if (refs.testbedCamera != null)
		{
			scope.Set(() => refs.testbedCamera.inputEnabled,
				v => refs.testbedCamera.inputEnabled = v, false);
		}

		if (refs.timeController != null)
		{
			scope.Set(() => refs.timeController.inputEnabled,
				v => refs.timeController.inputEnabled = v, false);
			// IMGUI allocates and calls CalcSize every OnGUI pass.
			scope.Set(() => refs.timeController.showOverlay,
				v => refs.timeController.showOverlay = v, false);
		}

		// ---- animation --------------------------------------------------------
		// The sun otherwise integrates Time.deltaTime. Note SolarSystemManager re-derives
		// sun/earth/moon/stars from the T values every frame regardless of this flag, so
		// writing dayT is still sufficient to move the sun.
		if (refs.solarSystem != null)
		{
			scope.Set(() => refs.solarSystem.animate, v => refs.solarSystem.animate = v, false);
		}

		// ---- country UI -------------------------------------------------------
		// One GameObject carries all three components; deactivating it also clears the
		// COUNTRY_HIGHLIGHT_ON keyword from the terrain shader.
		if (refs.countryInteraction != null && refs.countryInteraction.activeSelf)
		{
			GameObject go = refs.countryInteraction;
			scope.Set(() => go.activeSelf, v => go.SetActive(v), false);
		}

		// ---- terrain LOD ------------------------------------------------------
		// numFramesPerUpdate = 1 makes LOD a pure function of the current camera pose.
		// At the shipping value of 8 the visible set is a mixture of up to 8 camera
		// positions carried by a round-robin cursor, so it differs between runs at
		// different frame rates. ForceHighRes would also be deterministic but would
		// replace the terrain workload with an unrepresentative one, inflating the shared
		// cost the renderer delta is measured against.
		//
		// The component is deliberately NOT disabled: it subscribes Camera.onPreCull in
		// Start and unsubscribes only in OnDestroy, so disabling it would not stop it.
		if (refs.lodSystem != null)
		{
			SimpleLodSystem lod = refs.lodSystem;
			scope.Set(() => lod.numFramesPerUpdate, v => lod.numFramesPerUpdate = v,
				Mathf.Max(1, settings.lodFramesPerUpdate));
		}

		// ---- post-processing effect states -----------------------------------
		PinEffectStates(refs, scope, warnings);

		if (Application.isEditor) { warnings.Add("EDITOR_RUN"); }

		return scope;
	}

	/// <summary>
	/// Snapshots enabled state for EVERY effect in the chain, not only the ones a profile
	/// touches.
	///
	/// PostProcessingEffect.enabled is a serialized field on a ScriptableObject asset, so a
	/// change made in play mode stays in memory and the next project save writes it to
	/// disk. Snapshotting the whole array means a run cannot leave the chain in a state
	/// that differs from what was authored, whichever effects a profile happened to flip.
	/// </summary>
	static void PinEffectStates(BenchmarkSceneRefs refs, RestoreScope scope, List<string> warnings)
	{
		if (refs.postProcessing == null || refs.postProcessing.effects == null) { return; }

		foreach (PostProcessingEffect effect in refs.postProcessing.effects)
		{
			if (effect == null) { continue; }

			PostProcessingEffect captured = effect;
			bool previous = captured.enabled;
			scope.Add(() => captured.enabled = previous);
		}

		EffectStateGuard.Arm(refs.postProcessing.effects);
		scope.Add(EffectStateGuard.Disarm);
	}

	/// <summary>
	/// Conditions that would make a run untrustworthy. Separate from Pin so a caller can
	/// check before committing to a run.
	/// </summary>
	public static void Validate(BenchmarkSceneRefs refs, Settings settings, List<string> warnings)
	{
		if (!SystemInfo.supportsAsyncGPUReadback) { warnings.Add("NO_ASYNC_READBACK"); }

		if (Application.isEditor && settings.pinResolution)
		{
			// Screen.SetResolution does nothing in the editor; the Game view size wins.
			warnings.Add("RESOLUTION_NOT_PINNED_IN_EDITOR");
		}

		if (refs.solarSystem != null && !refs.solarSystem.geocentric)
		{
			// SolarTime's whole derivation assumes geocentric mode.
			warnings.Add("NOT_GEOCENTRIC");
		}

		// A baked baseline whose inputs have moved on still renders, still looks plausible, and
		// silently measures the staleness rather than the technique. This puts it in warnings[]
		// next to the numbers it invalidates.
		AtmosphereEffect atmosphere = FindAtmosphere(refs);
		Clouds.CloudEffect clouds = FindClouds(refs);
		foreach (string stale in SkyBakeStamp.FindStale(atmosphere, refs.baselineSky, clouds))
		{
			warnings.Add($"BAKE_STALE:{stale}");
		}
	}

	/// <summary>
	/// The volumetric cloud effect, for checking the captured baseline layers against it. Found by
	/// scanning the chain rather than held as a scene reference, the same way the atmosphere is -
	/// an effect is an asset, not a scene object.
	/// </summary>
	static Clouds.CloudEffect FindClouds(BenchmarkSceneRefs refs)
	{
		if (refs.postProcessing == null || refs.postProcessing.effects == null) { return null; }

		foreach (PostProcessingEffect effect in refs.postProcessing.effects)
		{
			if (effect is Clouds.CloudEffect clouds) { return clouds; }
		}
		return null;
	}

	static AtmosphereEffect FindAtmosphere(BenchmarkSceneRefs refs)
	{
		if (refs.postProcessing == null || refs.postProcessing.effects == null) { return null; }

		foreach (PostProcessingEffect effect in refs.postProcessing.effects)
		{
			if (effect is AtmosphereEffect atmosphere) { return atmosphere; }
		}
		return null;
	}
}
