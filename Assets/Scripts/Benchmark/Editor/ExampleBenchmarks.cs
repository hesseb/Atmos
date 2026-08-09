using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Authors a starting set of benchmarks.
///
/// Generated in code rather than hand-authored so the assets cannot come out malformed,
/// and so the reasoning behind each one lives next to it.
///
/// The set is chosen to span what actually drives the atmosphere's cost, which is not
/// uniform across viewpoints:
///   - The sky raymarch early-outs on rays that miss the atmosphere, so cost falls as the
///     planet shrinks in frame.
///   - The aerial-perspective composite skips sky pixels, so its cost scales with the
///     terrain-to-sky ratio - the opposite direction.
///   - There is no transmittance-based early-out, so a dense low-altitude view costs the
///     same per ray as a thin high one.
/// A benchmark that only ever looks at sky, or only ever at ground, measures one end of
/// that and reports it as the answer.
/// </summary>
static class ExampleBenchmarks
{
	const string Folder = "Assets/Data/Benchmarks";

	// Alpine terrain: mountainous, so LOD transitions and the height-displaced border
	// geometry are genuinely exercised rather than sitting over flat ocean.
	static readonly CoordinateDegrees Alps = new CoordinateDegrees(8.0f, 46.5f);
	// Arid and low-relief, the contrasting terrain case.
	static readonly CoordinateDegrees Sahara = new CoordinateDegrees(10.0f, 25.0f);

	[MenuItem("Testbed/Benchmark/Create Example Benchmarks")]
	static void Create()
	{
		System.IO.Directory.CreateDirectory(Folder);

		CreateSmoke();
		CreateFraming();
		CreateAltitude();
		CreateOrbit();
		CreateDayCycle();

		AssetDatabase.SaveAssets();
		AssetDatabase.Refresh();
		Debug.Log($"[Benchmark] wrote 5 example benchmarks to {Folder}");
	}

	// ------------------------------------------------------------------ the set

	/// <summary>Short, for iterating on the harness itself. Not for results.</summary>
	static void CreateSmoke()
	{
		var def = New("smoke", "Two short holds. For checking the harness runs end to end; " +
			"too few frames for a trustworthy 1% low.");
		def.bootFrames = 30;
		def.warmupFrames = 60;
		def.flushFrames = 16;

		def.segments = new[]
		{
			Hold("tilted", View(Alps, altitude: 25, pitch: 55), 200, Noon()),
			Hold("nadir", View(Alps, altitude: 25, pitch: 90), 200, Noon())
		};
		Save(def);
	}

	/// <summary>
	/// Same position and sun, four framings. The cleanest attribution of cost to sky
	/// fraction there is, because nothing else varies between the segments.
	/// </summary>
	static void CreateFraming()
	{
		var def = New("framing", "Four holds from one position, varying only pitch. Isolates " +
			"the effect of sky-to-terrain ratio on atmosphere cost with everything else held " +
			"fixed. Sky fraction runs roughly 0.0 at nadir to 0.9 at the horizon.");

		def.segments = new[]
		{
			Hold("nadir", View(Alps, altitude: 20, pitch: 90), 500, Noon()),
			Hold("steep", View(Alps, altitude: 20, pitch: 60), 500, Noon()),
			Hold("oblique", View(Alps, altitude: 20, pitch: 30), 500, Noon()),
			Hold("horizon", View(Alps, altitude: 20, pitch: 2), 500, Noon())
		};
		Save(def);
	}

	/// <summary>
	/// Climb from near-ground to high orbit. Sweeps sky fraction continuously and puts the
	/// terrain LOD system through its full range, which is the main shared cost both
	/// renderers carry.
	/// </summary>
	static void CreateAltitude()
	{
		var def = New("altitude", "Continuous climb from altitude 4 to 220 at a fixed nadir-ish " +
			"pitch. Sweeps sky fraction and exercises the whole LOD range. Altitude " +
			"interpolates logarithmically, so the apparent zoom rate is constant.");

		def.segments = new[]
		{
			Interpolate("climb",
				View(Alps, altitude: 4, pitch: 75),
				View(Alps, altitude: 220, pitch: 75),
				900, Noon()),
			Interpolate("descend",
				View(Alps, altitude: 220, pitch: 75),
				View(Alps, altitude: 4, pitch: 75),
				900, Noon())
		};
		Save(def);
	}

	/// <summary>
	/// A full rotation at a typical strategy-game framing. Covers the whole terrain set
	/// rather than one tile, so it is the closest of these to a representative workload.
	/// </summary>
	static void CreateOrbit()
	{
		var def = New("orbit", "360 degree heading sweep at a typical strategy-game framing, " +
			"over two different terrain types. The nearest thing here to a representative " +
			"workload rather than an isolated condition.");

		def.segments = new[]
		{
			Orbit("alps", View(Alps, altitude: 30, pitch: 55), 360f, 1200, Noon()),
			Orbit("sahara", View(Sahara, altitude: 30, pitch: 55), 360f, 1200, Noon())
		};
		Save(def);
	}

	/// <summary>
	/// The RQ1 benchmark. Twilight is where ozone absorption and the Mie forward lobe do
	/// most of their visible work, so it is where a physically based sky should differ most
	/// from a cheap one - both in appearance and, because the sun angle changes what the
	/// raymarch integrates, potentially in cost.
	/// </summary>
	static void CreateDayCycle()
	{
		var def = New("daycycle", "Fixed camera near the horizon while the sun sets from +30 " +
			"degrees elevation through to -12 (civil twilight). Isolates sun angle with the " +
			"view held constant. Screenshots at the ends of each segment give paired images " +
			"for the visual comparison.");

		TestbedCamera.CameraView view = View(Alps, altitude: 12, pitch: 8);

		def.segments = new[]
		{
			SunSweep("daylight", view, Elevation(30f), Elevation(10f), 600),
			SunSweep("goldenhour", view, Elevation(10f), Elevation(0f), 600),
			SunSweep("twilight", view, Elevation(0f), Elevation(-12f), 600)
		};

		for (int i = 0; i < def.segments.Length; i++)
		{
			def.segments[i].screenshotFirstAndLast = true;
		}
		Save(def);
	}

	// ------------------------------------------------------------------ helpers

	static BenchmarkDefinition New(string id, string description)
	{
		var def = ScriptableObject.CreateInstance<BenchmarkDefinition>();
		def.id = id;
		def.description = description;
		def.bootFrames = 60;
		def.prewarmPosesEvery = 40;
		def.warmupFrames = 120;
		def.defaultSettleFrames = 12;
		def.flushFrames = 16;
		def.simulatedFps = 60f;
		def.monthT = 0.404f;
		def.yearT = 0.288f;
		return def;
	}

	static void Save(BenchmarkDefinition def)
	{
		string path = $"{Folder}/{def.id}.asset";
		AssetDatabase.DeleteAsset(path);
		AssetDatabase.CreateAsset(def, path);
	}

	static TestbedCamera.CameraView View(CoordinateDegrees coordinate, float altitude, float pitch,
		float heading = 0f, float fov = 60f)
	{
		return new TestbedCamera.CameraView
		{
			coordinate = coordinate,
			altitude = altitude,
			pitch = pitch,
			heading = heading,
			roll = 0f,
			fieldOfView = fov
		};
	}

	static ViewRef Inline(TestbedCamera.CameraView view)
	{
		// Inline rather than a bookmark reference: a benchmark that resolves through a live
		// bookmark stops being reproducible the moment someone re-captures that slot.
		return new ViewRef { source = ViewRef.SourceKind.Inline, inlineView = view };
	}

	// Local solar noon at the observer, solved from the geometry rather than a magic dayT,
	// so the sun is in the same place regardless of where the camera is.
	static SunKey Noon() => new SunKey { mode = SunKey.ModeKind.Extreme, highest = true };

	static SunKey Elevation(float degrees) => new SunKey
	{
		mode = SunKey.ModeKind.Elevation,
		elevationDegrees = degrees,
		rising = false
	};

	static BenchmarkSegment Base(string label, SegmentKind kind, int frames, SunKey sun)
	{
		return new BenchmarkSegment
		{
			label = label,
			kind = kind,
			frames = frames,
			settleFramesOverride = 0,   // 0 means "use the definition default"
			ease = EaseMode.Linear,     // constant rate, so frames sample poses evenly
			sunFrom = sun,
			sunTo = sun,
			screenshotFrames = new int[0]
		};
	}

	static BenchmarkSegment Hold(string label, TestbedCamera.CameraView view, int frames, SunKey sun)
	{
		BenchmarkSegment s = Base(label, SegmentKind.Hold, frames, sun);
		s.from = Inline(view);
		s.to = Inline(view);
		return s;
	}

	static BenchmarkSegment Interpolate(string label, TestbedCamera.CameraView from,
		TestbedCamera.CameraView to, int frames, SunKey sun)
	{
		BenchmarkSegment s = Base(label, SegmentKind.Interpolate, frames, sun);
		s.from = Inline(from);
		s.to = Inline(to);
		return s;
	}

	static BenchmarkSegment Orbit(string label, TestbedCamera.CameraView from, float sweepDegrees,
		int frames, SunKey sun)
	{
		BenchmarkSegment s = Base(label, SegmentKind.Orbit, frames, sun);
		s.from = Inline(from);
		s.to = Inline(from);   // endpoint is derived from the sweep at plan time
		s.sweepDegrees = sweepDegrees;
		s.sweepAxis = OrbitAxis.Heading;
		return s;
	}

	static BenchmarkSegment SunSweep(string label, TestbedCamera.CameraView view, SunKey from,
		SunKey to, int frames)
	{
		BenchmarkSegment s = Base(label, SegmentKind.TimeOfDay, frames, from);
		s.from = Inline(view);
		s.to = Inline(view);
		s.sunFrom = from;
		s.sunTo = to;
		return s;
	}
}
