using UnityEngine;

public enum BenchmarkPhase
{
	// Absorbs LoadingManager's synchronous world bootstrap; frame 0 is hundreds of ms.
	Boot,
	// Steps a decimated set of the run's own poses so D3D11 pipeline states are created
	// before anything is measured. There is no ShaderVariantCollection in this project.
	Prewarm,
	// Holds the first pose: covers the one-frame stale-LUT window after a renderer change
	// and lets GPU clocks settle.
	Warmup,
	// Absorbs LOD / atmosphere-LUT / sun-colour lag across a pose discontinuity.
	Settle,
	// The data.
	Measure,
	// Holds the last pose so the frame-timing tail drains and the final measured frames
	// receive their GPU times.
	Flush
}

public enum SegmentKind
{
	/// <summary>Move from one view to another.</summary>
	Interpolate,
	/// <summary>Sit still. The cleanest measurement available - no motion, no LOD churn.</summary>
	Hold,
	/// <summary>Rotate around the globe at fixed altitude and pitch.</summary>
	Orbit,
	/// <summary>Camera fixed, sun advances. Isolates the atmosphere's sun-angle cost.</summary>
	TimeOfDay
}

public enum OrbitAxis { Heading, Longitude }

public enum EaseMode
{
	/// <summary>Constant rate. Preferred for measurement - every frame samples an equally
	/// spaced pose, so per-frame statistics are not weighted toward the ends.</summary>
	Linear,
	SmoothStep
}

[System.Serializable]
public struct ViewRef
{
	public enum SourceKind { Inline, Bookmark }

	public SourceKind source;
	public TestbedCamera.CameraView inlineView;
	public CameraBookmarks bookmarkAsset;
	public string bookmarkLabel;
	public int bookmarkIndex;
}

[System.Serializable]
public struct SunKey
{
	public enum ModeKind
	{
		/// <summary>Raw time-of-day value.</summary>
		DayT,
		/// <summary>Solve for the sun at a given elevation above the observer's horizon.</summary>
		Elevation,
		/// <summary>Local solar noon or midnight.</summary>
		Extreme
	}

	public ModeKind mode;
	public float dayT;
	public float elevationDegrees;
	public bool rising;
	public bool highest;

	public static SunKey FromDayT(float dayT) => new SunKey { mode = ModeKind.DayT, dayT = dayT };
}

[System.Serializable]
public struct BenchmarkSegment
{
	public string label;
	public SegmentKind kind;
	[Min(1)] public int frames;
	// 0 (the default for a serialized int) uses the definition's default. Only a positive
	// value overrides - otherwise every segment authored in the inspector would silently
	// get zero settle frames, and a pose discontinuity would land in the statistics.
	[Min(0)] public int settleFramesOverride;

	public ViewRef from;
	public ViewRef to;
	public EaseMode ease;

	[Header("Orbit")]
	public float sweepDegrees;
	public OrbitAxis sweepAxis;

	[Header("Sun")]
	public SunKey sunFrom;
	public SunKey sunTo;

	[Header("Screenshots")]
	public bool screenshotFirstAndLast;
	public int[] screenshotFrames;
}

/// <summary>
/// A reproducible camera-and-sun script for the benchmark harness.
///
/// All four segment kinds collapse to the same evaluator - a segment is always
/// "interpolate a (view, sun) pair from A to B over N frames"; the kind only decides how B
/// is derived from A when the plan is built. That is what lets the whole run be flattened
/// into a fixed array of frames before a single one is rendered, which in turn is what
/// makes "both renderers saw identical poses" provable by hash rather than merely intended.
/// </summary>
[CreateAssetMenu(menuName = "Testbed/Benchmark Definition", fileName = "Benchmark")]
public class BenchmarkDefinition : ScriptableObject
{
	public string id = "benchmark";
	[TextArea(2, 5)] public string description;

	[Header("Phase lengths (frames)")]
	public int bootFrames = 60;
	// One prewarm frame per N frames of content.
	[Min(1)] public int prewarmPosesEvery = 40;
	public int warmupFrames = 120;
	public int defaultSettleFrames = 6;
	public int flushFrames = 16;

	[Header("Simulation")]
	// Time.captureDeltaTime is set to 1/this. Measured: it fixes Time.deltaTime without
	// throttling, so the run advances as fast as the hardware allows.
	public float simulatedFps = 60f;
	[Range(0f, 1f)] public float monthT = 0.404f;
	[Range(0f, 1f)] public float yearT = 0.288f;

	public BenchmarkSegment[] segments;

	public int SettleFramesFor(BenchmarkSegment segment)
	{
		return segment.settleFramesOverride > 0 ? segment.settleFramesOverride : defaultSettleFrames;
	}
}
