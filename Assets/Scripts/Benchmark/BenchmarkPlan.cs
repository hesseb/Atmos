using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using SolarSystem;

/// <summary>One fully resolved frame. Everything the runner needs, precomputed.</summary>
public struct PlannedFrame
{
	public BenchmarkPhase phase;
	public int segmentIndex;      // -1 outside a segment
	public int segmentFrame;
	public TestbedCamera.CameraView view;
	public float dayT, monthT, yearT;
	public bool screenshot;
	// Fraction of the frame that misses the planet, sampled on a 32x32 grid at plan time.
	// The covariate that explains why the atmosphere is expensive at a given frame: its
	// sky raymarch early-outs on rays that miss, and the composite skips sky pixels.
	public float skyFraction;
}

/// <summary>
/// A benchmark resolved to a flat, immutable array of frames.
///
/// Building this up front is the design's load-bearing decision. Because every phase has a
/// fixed frame count, the frame index of every measured pose is identical across runs and
/// across renderer configurations - and so is Time.time, which is what keeps the ocean's
/// wave phase matched between them. And because the array is hashable, "both renderers
/// rendered the same poses" becomes something to check rather than to assume.
/// </summary>
public class BenchmarkPlan
{
	public BenchmarkDefinition definition;
	public PlannedFrame[] frames;
	public string[] segmentLabels;
	public ulong planHash;
	public float surfaceRadius;
	public float aspect;
	public bool usedLiveBookmarks;
	public List<string> warnings = new List<string>();

	public int Length => frames != null ? frames.Length : 0;
	public float CaptureDeltaTime => 1f / Mathf.Max(1f, definition.simulatedFps);

	// ------------------------------------------------------------------ building

	public static BenchmarkPlan Build(BenchmarkDefinition definition, EarthOrbit earth,
		float surfaceRadius, float aspect, CameraBookmarks fallbackBookmarks)
	{
		var plan = new BenchmarkPlan
		{
			definition = definition,
			surfaceRadius = surfaceRadius,
			aspect = aspect
		};

		if (definition == null || definition.segments == null || definition.segments.Length == 0)
		{
			plan.warnings.Add("EMPTY_BENCHMARK");
			plan.frames = new PlannedFrame[0];
			plan.segmentLabels = new string[0];
			return plan;
		}

		// --- content: settle + measure frames for each segment ---
		var content = new List<PlannedFrame>();
		var labels = new List<string>();

		for (int s = 0; s < definition.segments.Length; s++)
		{
			BenchmarkSegment segment = definition.segments[s];
			labels.Add(string.IsNullOrEmpty(segment.label) ? $"segment{s}" : segment.label);

			TestbedCamera.CameraView fromView = plan.ResolveView(segment.from, fallbackBookmarks);
			TestbedCamera.CameraView toView = DeriveEndpoint(segment, fromView);

			float fromDay = plan.ResolveSun(segment.sunFrom, fromView, earth, definition);
			float toDay = segment.kind == SegmentKind.TimeOfDay
				? plan.ResolveSun(segment.sunTo, toView, earth, definition)
				: fromDay;

			int settle = Mathf.Max(0, definition.SettleFramesFor(segment));
			int measure = Mathf.Max(1, segment.frames);
			bool sweep = segment.kind == SegmentKind.Orbit;

			// Settle frames sit at the segment's starting state. They are recorded but not
			// measured, so a pose discontinuity at a boundary never lands in the statistics.
			for (int f = 0; f < settle; f++)
			{
				content.Add(plan.MakeFrame(BenchmarkPhase.Settle, s, f, fromView, fromDay, definition, false));
			}

			for (int f = 0; f < measure; f++)
			{
				float t = measure == 1 ? 0f : (float)f / (measure - 1);
				t = Ease(t, segment.ease);

				TestbedCamera.CameraView view = LerpView(fromView, toView, t, sweep);
				float dayT = LerpDayT(fromDay, toDay, t);
				bool shot = IsScreenshotFrame(segment, f, measure);

				content.Add(plan.MakeFrame(BenchmarkPhase.Measure, s, f, view, dayT, definition, shot));
			}
		}

		// --- assemble: boot + prewarm + warmup + content + flush ---
		var all = new List<PlannedFrame>();

		PlannedFrame first = content[0];
		PlannedFrame last = content[content.Count - 1];

		AddRepeated(all, first, BenchmarkPhase.Boot, Mathf.Max(0, definition.bootFrames));

		// Prewarm walks a decimated set of the run's own poses so every terrain tile
		// variant and both extremes of the raymarch create their pipeline states before
		// anything is measured. On screen this looks like the camera teleporting through
		// the whole route in a fraction of a second, which is what it is doing.
		//
		// 0 means off, and must not fall through to a stride of 1 - that would prewarm
		// every single pose, i.e. render the entire run twice.
		if (definition.prewarmPosesEvery > 0)
		{
			for (int i = 0; i < content.Count; i += definition.prewarmPosesEvery)
			{
				PlannedFrame f = content[i];
				f.phase = BenchmarkPhase.Prewarm;
				f.screenshot = false;
				all.Add(f);
			}
		}

		AddRepeated(all, first, BenchmarkPhase.Warmup, Mathf.Max(0, definition.warmupFrames));
		all.AddRange(content);
		AddRepeated(all, last, BenchmarkPhase.Flush, Mathf.Max(0, definition.flushFrames));

		plan.frames = all.ToArray();
		plan.segmentLabels = labels.ToArray();
		plan.planHash = plan.ComputeHash();
		return plan;
	}

	static void AddRepeated(List<PlannedFrame> into, PlannedFrame template, BenchmarkPhase phase, int count)
	{
		template.phase = phase;
		template.screenshot = false;
		for (int i = 0; i < count; i++) { into.Add(template); }
	}

	PlannedFrame MakeFrame(BenchmarkPhase phase, int segmentIndex, int segmentFrame,
		TestbedCamera.CameraView view, float dayT, BenchmarkDefinition definition, bool screenshot)
	{
		return new PlannedFrame
		{
			phase = phase,
			segmentIndex = segmentIndex,
			segmentFrame = segmentFrame,
			view = view,
			dayT = dayT,
			monthT = definition.monthT,
			yearT = definition.yearT,
			screenshot = screenshot,
			skyFraction = EstimateSkyFraction(view, surfaceRadius, aspect)
		};
	}

	// ------------------------------------------------------- endpoint derivation

	/// <summary>
	/// Where segment B comes from. This is the only place the four kinds differ; after
	/// this they all take the same interpolation path.
	/// </summary>
	static TestbedCamera.CameraView DeriveEndpoint(BenchmarkSegment segment, TestbedCamera.CameraView from)
	{
		switch (segment.kind)
		{
			case SegmentKind.Hold:
			case SegmentKind.TimeOfDay:
				return from;

			case SegmentKind.Orbit:
			{
				TestbedCamera.CameraView to = from;
				if (segment.sweepAxis == OrbitAxis.Heading)
				{
					to.heading = from.heading + segment.sweepDegrees;
				}
				else
				{
					to.coordinate.longitude = from.coordinate.longitude + segment.sweepDegrees;
				}
				return to;
			}

			default:
				return from;
		}
	}

	// ------------------------------------------------------------ interpolation

	static float Ease(float t, EaseMode mode)
	{
		t = Mathf.Clamp01(t);
		return mode == EaseMode.SmoothStep ? t * t * (3f - 2f * t) : t;
	}

	/// <summary>
	/// Interpolates a view. Altitude is interpolated in log space so a zoom reads as a
	/// constant rate rather than rushing at one end.
	///
	/// <paramref name="sweep"/> selects how angles travel, and the distinction matters:
	/// an Orbit segment's endpoints are the start plus a sweep, so a full 360 degree sweep
	/// has *identical* endpoints. Shortest-arc or slerp would correctly conclude there is
	/// nowhere to go and the camera would stand still for the whole segment. Sweeps
	/// therefore interpolate the raw angle; everything else takes the short way round.
	/// </summary>
	public static TestbedCamera.CameraView LerpView(TestbedCamera.CameraView a,
		TestbedCamera.CameraView b, float t, bool sweep)
	{
		if (t <= 0f) { return a; }
		if (t >= 1f && !sweep) { return b; }

		CoordinateDegrees coordinate;
		if (sweep)
		{
			coordinate = new CoordinateDegrees(
				a.coordinate.longitude + (b.coordinate.longitude - a.coordinate.longitude) * t,
				Mathf.Lerp(a.coordinate.latitude, b.coordinate.latitude, t));
		}
		else
		{
			// Great circle, matching how the camera's own panning moves.
			Vector3 pa = GeoMaths.CoordinateToPoint(a.coordinate.ConvertToRadians(), 1f);
			Vector3 pb = GeoMaths.CoordinateToPoint(b.coordinate.ConvertToRadians(), 1f);
			Vector3 p = Vector3.Slerp(pa, pb, t).normalized;
			coordinate = GeoMaths.PointToCoordinate(p).ConvertToDegrees();
		}

		float altitudeA = Mathf.Max(a.altitude, 0.001f);
		float altitudeB = Mathf.Max(b.altitude, 0.001f);

		float heading = sweep
			? a.heading + (b.heading - a.heading) * t
			: a.heading + Mathf.DeltaAngle(a.heading, b.heading) * t;

		return new TestbedCamera.CameraView
		{
			coordinate = coordinate,
			altitude = Mathf.Exp(Mathf.Lerp(Mathf.Log(altitudeA), Mathf.Log(altitudeB), t)),
			pitch = Mathf.Lerp(a.pitch, b.pitch, t),
			heading = heading,
			roll = a.roll + Mathf.DeltaAngle(a.roll, b.roll) * t,
			fieldOfView = Mathf.Lerp(a.fieldOfView, b.fieldOfView, t)
		};
	}

	/// <summary>
	/// Interpolates time of day forward. A sweep from evening to morning must advance
	/// through midnight rather than run backwards to reach a nearer value.
	/// </summary>
	public static float LerpDayT(float from, float to, float t)
	{
		float delta = Mathf.Repeat(to - from, 1f);
		return Mathf.Repeat(from + delta * t, 1f);
	}

	// ------------------------------------------------------------- sky fraction

	/// <summary>
	/// Fraction of the frame whose view rays miss the planet, on a 32x32 grid.
	///
	/// Note pitch 0 is not "horizon centred": it aims along the local tangent, which sits
	/// above the true horizon by acos(R/(R+altitude)) - about 20 degrees at altitude 10 -
	/// so a pitch-0 view is mostly sky.
	/// </summary>
	public static float EstimateSkyFraction(TestbedCamera.CameraView view, float surfaceRadius,
		float aspect, int grid = 32)
	{
		TestbedCamera.ComputePose(view, surfaceRadius, out Vector3 position, out Quaternion rotation);

		Vector3 forward = rotation * Vector3.forward;
		Vector3 up = rotation * Vector3.up;
		Vector3 right = rotation * Vector3.right;

		float tanHalf = Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad);
		int misses = 0;
		int total = grid * grid;

		for (int y = 0; y < grid; y++)
		{
			float ndcY = ((y + 0.5f) / grid) * 2f - 1f;
			for (int x = 0; x < grid; x++)
			{
				float ndcX = ((x + 0.5f) / grid) * 2f - 1f;
				Vector3 dir = (forward + right * (ndcX * tanHalf * aspect) + up * (ndcY * tanHalf)).normalized;
				if (!RayHitsSphere(position, dir, surfaceRadius)) { misses++; }
			}
		}

		return (float)misses / total;
	}

	static bool RayHitsSphere(Vector3 origin, Vector3 direction, float radius)
	{
		float b = Vector3.Dot(origin, direction);
		float c = Vector3.Dot(origin, origin) - radius * radius;
		float discriminant = b * b - c;
		if (discriminant < 0f) { return false; }

		float sqrtDisc = Mathf.Sqrt(discriminant);
		float t = -b - sqrtDisc;
		if (t < 0f) { t = -b + sqrtDisc; }
		return t >= 0f;
	}

	// --------------------------------------------------------------- resolution

	TestbedCamera.CameraView ResolveView(ViewRef reference, CameraBookmarks fallback)
	{
		if (reference.source == ViewRef.SourceKind.Inline) { return reference.inlineView; }

		CameraBookmarks bookmarks = reference.bookmarkAsset != null ? reference.bookmarkAsset : fallback;
		if (bookmarks == null)
		{
			warnings.Add("BOOKMARK_ASSET_MISSING");
			return reference.inlineView;
		}

		int index = reference.bookmarkIndex;
		if (!string.IsNullOrEmpty(reference.bookmarkLabel))
		{
			for (int i = 0; i < bookmarks.Count; i++)
			{
				if (bookmarks.LabelAt(i) == reference.bookmarkLabel) { index = i; break; }
			}
		}

		if (!bookmarks.TryGetView(index, out TestbedCamera.CameraView view))
		{
			warnings.Add($"BOOKMARK_UNASSIGNED:{reference.bookmarkLabel}:{index}");
			return reference.inlineView;
		}

		// Recorded so a published run can say whether any viewpoint came from a live
		// bookmark that someone could since have overwritten.
		usedLiveBookmarks = true;
		return view;
	}

	float ResolveSun(SunKey key, TestbedCamera.CameraView view, EarthOrbit earth, BenchmarkDefinition definition)
	{
		if (earth == null || key.mode == SunKey.ModeKind.DayT) { return Mathf.Repeat(key.dayT, 1f); }

		Vector3 observerUp = GeoMaths.CoordinateToPoint(view.coordinate.ConvertToRadians(), 1f);

		if (key.mode == SunKey.ModeKind.Extreme)
		{
			return SolarTime.SolveDayTForExtreme(earth, observerUp, definition.yearT, key.highest);
		}

		if (SolarTime.TrySolveDayT(earth, observerUp, definition.yearT,
				key.elevationDegrees, key.rising, out float dayT))
		{
			return dayT;
		}

		// Unreachable at this latitude and time of year, or the observer is at a pole.
		warnings.Add($"SUN_ELEVATION_UNREACHABLE:{key.elevationDegrees}");
		return Mathf.Repeat(key.dayT, 1f);
	}

	static bool IsScreenshotFrame(BenchmarkSegment segment, int frame, int total)
	{
		if (segment.screenshotFirstAndLast && (frame == 0 || frame == total - 1)) { return true; }
		if (segment.screenshotFrames == null) { return false; }

		for (int i = 0; i < segment.screenshotFrames.Length; i++)
		{
			if (segment.screenshotFrames[i] == frame) { return true; }
		}
		return false;
	}

	// ------------------------------------------------------------------ hashing

	/// <summary>
	/// FNV-1a over quantized values. Quantized because raw float bits can differ across
	/// compiler and platform versions, which would make the hash useless as a
	/// cross-machine equality check.
	/// </summary>
	public ulong ComputeHash()
	{
		ulong hash = 0xcbf29ce484222325UL;

		Mix(ref hash, (ulong)frames.Length);
		for (int i = 0; i < frames.Length; i++)
		{
			PlannedFrame f = frames[i];
			Mix(ref hash, (ulong)(int)f.phase);
			Mix(ref hash, (ulong)(f.segmentIndex + 1));
			MixQuantized(ref hash, f.view.coordinate.longitude, 1000f);
			MixQuantized(ref hash, f.view.coordinate.latitude, 1000f);
			MixQuantized(ref hash, f.view.altitude, 10000f);
			MixQuantized(ref hash, f.view.pitch, 1000f);
			MixQuantized(ref hash, f.view.heading, 1000f);
			MixQuantized(ref hash, f.view.roll, 1000f);
			MixQuantized(ref hash, f.view.fieldOfView, 1000f);
			MixQuantized(ref hash, f.dayT, 100000f);
			MixQuantized(ref hash, f.monthT, 100000f);
			MixQuantized(ref hash, f.yearT, 100000f);
			Mix(ref hash, f.screenshot ? 1UL : 0UL);
		}
		return hash;
	}

	static void MixQuantized(ref ulong hash, float value, float scale)
	{
		Mix(ref hash, unchecked((ulong)(long)Mathf.Round(value * scale)));
	}

	static void Mix(ref ulong hash, ulong value)
	{
		for (int shift = 0; shift < 64; shift += 8)
		{
			hash ^= (value >> shift) & 0xFF;
			hash *= 0x100000001b3UL;
		}
	}

	// ------------------------------------------------------------------- output

	public string ToCsv()
	{
		var sb = new StringBuilder(frames.Length * 140);
		sb.Append("frame_index,phase,segment_index,segment_label,segment_frame,")
		  .Append("cam_lon,cam_lat,cam_alt,cam_pitch,cam_heading,cam_roll,cam_fov,")
		  .Append("sun_dayT,sun_monthT,sun_yearT,sky_fraction,screenshot\n");

		var ci = CultureInfo.InvariantCulture;
		for (int i = 0; i < frames.Length; i++)
		{
			PlannedFrame f = frames[i];
			string label = f.segmentIndex >= 0 && f.segmentIndex < segmentLabels.Length
				? segmentLabels[f.segmentIndex] : "";

			sb.Append(i.ToString(ci)).Append(',')
			  .Append(f.phase).Append(',')
			  .Append(f.segmentIndex.ToString(ci)).Append(',')
			  .Append(label).Append(',')
			  .Append(f.segmentFrame.ToString(ci)).Append(',')
			  .Append(f.view.coordinate.longitude.ToString("G9", ci)).Append(',')
			  .Append(f.view.coordinate.latitude.ToString("G9", ci)).Append(',')
			  .Append(f.view.altitude.ToString("G9", ci)).Append(',')
			  .Append(f.view.pitch.ToString("G9", ci)).Append(',')
			  .Append(f.view.heading.ToString("G9", ci)).Append(',')
			  .Append(f.view.roll.ToString("G9", ci)).Append(',')
			  .Append(f.view.fieldOfView.ToString("G9", ci)).Append(',')
			  .Append(f.dayT.ToString("G9", ci)).Append(',')
			  .Append(f.monthT.ToString("G9", ci)).Append(',')
			  .Append(f.yearT.ToString("G9", ci)).Append(',')
			  .Append(f.skyFraction.ToString("G9", ci)).Append(',')
			  .Append(f.screenshot ? '1' : '0').Append('\n');
		}
		return sb.ToString();
	}

	public string Describe()
	{
		int measured = 0, screenshots = 0;
		var byPhase = new int[System.Enum.GetValues(typeof(BenchmarkPhase)).Length];
		foreach (PlannedFrame f in frames)
		{
			byPhase[(int)f.phase]++;
			if (f.phase == BenchmarkPhase.Measure) { measured++; }
			if (f.screenshot) { screenshots++; }
		}

		// Spelled out because prewarm is visually alarming - the camera teleports through
		// the whole route - and the first question anyone asks is whether that is a bug.
		var phases = new System.Text.StringBuilder();
		foreach (BenchmarkPhase phase in System.Enum.GetValues(typeof(BenchmarkPhase)))
		{
			if (byPhase[(int)phase] == 0) { continue; }
			if (phases.Length > 0) { phases.Append(" -> "); }
			phases.Append(phase).Append(' ').Append(byPhase[(int)phase]);
		}

		var ci = CultureInfo.InvariantCulture;
		return $"plan '{definition.id}': {frames.Length} frames total, {measured} measured, " +
			$"{screenshots} screenshots, {segmentLabels.Length} segments\n" +
			$"  phases: {phases}\n" +
			$"  plan_hash 0x{planHash:x16}   captureDeltaTime {CaptureDeltaTime.ToString("F5", ci)}" +
			(usedLiveBookmarks ? "\n  WARNING: resolved through live bookmarks - bake before publishing" : "") +
			(warnings.Count > 0 ? "\n  warnings: " + string.Join(", ", warnings) : "");
	}
}
