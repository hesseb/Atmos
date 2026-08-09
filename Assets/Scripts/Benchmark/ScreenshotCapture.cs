using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// The project defines its own global-namespace `Path` (a polygon path, in
// Assets/Scripts/Types/Shape.cs) which shadows the using-imported System.IO.Path.
using IOPath = System.IO.Path;

/// <summary>
/// Captures marked frames to PNG during a capture pass and writes a manifest pairing each
/// image with the frame that produced it.
///
/// Capture deliberately never happens during a timing pass.
/// <c>ScreenCapture.CaptureScreenshotAsTexture</c> forces a full GPU-to-CPU readback, which
/// stalls the pipeline for milliseconds and lands in the timing of that frame and the next -
/// it would corrupt exactly the measurement the harness exists to produce.
///
/// Splitting them is sound because world state at plan index <c>i</c> is a pure function of
/// <c>i</c>: the plan is replayed identically in every pass, so the image captured at frame
/// N in a capture run is the image the timing run rendered at frame N. That equivalence is
/// only as strong as the hashes both runs record - compare <c>plan_hash</c> and
/// <c>pose_hash</c> before putting a figure next to a number.
/// </summary>
public class ScreenshotCapture
{
	static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

	public struct Entry
	{
		public int frameIndex, segmentIndex, segmentFrame;
		public string passId, segmentLabel, file;
		public int width, height;
		public float dayT, sunElevationDeg, skyFraction;
		public TestbedCamera.CameraView view;
	}

	readonly string folder;
	readonly List<Entry> entries = new List<Entry>();

	public int Count => entries.Count;
	public int FailureCount { get; private set; }
	public string Folder => folder;

	public ScreenshotCapture(string runFolder)
	{
		folder = IOPath.Combine(runFolder, "screenshots");
		Directory.CreateDirectory(folder);
	}

	/// <summary>
	/// Reads the backbuffer and writes a PNG. Must be called after WaitForEndOfFrame -
	/// anywhere earlier and the frame is not finished, so the capture would be of the
	/// previous frame or of a partially drawn one.
	/// </summary>
	public void CaptureNow(int frameIndex, string passId, PlannedFrame plan, string segmentLabel,
		Vector3 cameraPosition, Vector3 sunDirection, Vector3 planetCentre)
	{
		// The no-argument overload is superSize 1, and it must stay that way: a supersized
		// capture re-renders at a different resolution, which changes both FXAA and the
		// atmosphere's per-pixel cost. It would no longer be the image that was measured.
		Texture2D texture = ScreenCapture.CaptureScreenshotAsTexture();
		if (texture == null)
		{
			Debug.LogWarning($"[Benchmark] screenshot capture returned no texture at frame {frameIndex}.");
			FailureCount++;
			return;
		}

		int width = texture.width;
		int height = texture.height;
		byte[] png = null;

		try
		{
			png = texture.EncodeToPNG();
		}
		finally
		{
			// CaptureScreenshotAsTexture allocates a new texture per call; leaking one per
			// screenshot would grow the run's memory footprint monotonically.
			Object.Destroy(texture);
		}

		if (png == null)
		{
			Debug.LogWarning($"[Benchmark] PNG encode failed at frame {frameIndex}.");
			FailureCount++;
			return;
		}

		string file = $"f{frameIndex:D6}_{Sanitise(passId)}_{Sanitise(segmentLabel)}.png";

		try
		{
			File.WriteAllBytes(IOPath.Combine(folder, file), png);
		}
		catch (IOException e)
		{
			Debug.LogWarning($"[Benchmark] could not write {file}: {e.Message}");
			FailureCount++;
			return;
		}

		entries.Add(new Entry
		{
			frameIndex = frameIndex,
			segmentIndex = plan.segmentIndex,
			segmentFrame = plan.segmentFrame,
			passId = passId,
			segmentLabel = segmentLabel,
			file = file,
			width = width,
			height = height,
			dayT = plan.dayT,
			sunElevationDeg = SunElevation(cameraPosition, sunDirection, planetCentre),
			skyFraction = plan.skyFraction,
			view = plan.view
		});
	}

	/// <summary>
	/// Sun elevation above the observer's local horizon, in degrees. Recorded because it is
	/// the caption a twilight figure needs, and deriving it after the fact would mean
	/// reconstructing the solar solve from dayT.
	/// </summary>
	static float SunElevation(Vector3 cameraPosition, Vector3 sunDirection, Vector3 planetCentre)
	{
		Vector3 up = cameraPosition - planetCentre;
		if (up.sqrMagnitude < 1e-8f || sunDirection.sqrMagnitude < 1e-8f) { return 0f; }

		return 90f - Vector3.Angle(up, sunDirection);
	}

	public void WriteManifest()
	{
		if (entries.Count == 0) { return; }

		var sb = new StringBuilder(entries.Count * 160);
		sb.Append("frame_index,pass_id,segment_index,segment_label,segment_frame,file,")
		  .Append("width,height,sun_dayT,sun_elevation_deg,sky_fraction,")
		  .Append("lon,lat,alt,pitch,heading,roll,fov\n");

		foreach (Entry e in entries)
		{
			sb.Append(e.frameIndex.ToString(Ci)).Append(',')
			  .Append(e.passId).Append(',')
			  .Append(e.segmentIndex.ToString(Ci)).Append(',')
			  .Append(e.segmentLabel).Append(',')
			  .Append(e.segmentFrame.ToString(Ci)).Append(',')
			  .Append(e.file).Append(',')
			  .Append(e.width.ToString(Ci)).Append(',')
			  .Append(e.height.ToString(Ci)).Append(',')
			  .Append(F(e.dayT)).Append(',')
			  .Append(F(e.sunElevationDeg)).Append(',')
			  .Append(F(e.skyFraction)).Append(',')
			  .Append(F(e.view.coordinate.longitude)).Append(',')
			  .Append(F(e.view.coordinate.latitude)).Append(',')
			  .Append(F(e.view.altitude)).Append(',')
			  .Append(F(e.view.pitch)).Append(',')
			  .Append(F(e.view.heading)).Append(',')
			  .Append(F(e.view.roll)).Append(',')
			  .Append(F(e.view.fieldOfView)).Append('\n');
		}

		File.WriteAllText(IOPath.Combine(folder, "manifest.csv"), sb.ToString());
	}

	// InvariantCulture throughout: this machine is sv-SE, where the default ToString emits
	// "1,5" and would silently produce a CSV with the wrong number of columns.
	static string F(float value) => value.ToString("F6", Ci);

	static string Sanitise(string value)
	{
		if (string.IsNullOrEmpty(value)) { return "unnamed"; }
		foreach (char c in IOPath.GetInvalidFileNameChars()) { value = value.Replace(c, '-'); }
		return value.Replace(' ', '-');
	}
}
