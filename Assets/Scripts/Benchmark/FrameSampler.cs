using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// Collects timing and counter data, one sample per frame.
///
/// Attribution note: a sample taken at the top of frame N describes frame **N-1**. Frame
/// timings arrive one frame late, and render counters are flushed at end of frame, so both
/// refer to the frame just completed. The caller is responsible for storing it against the
/// right row.
///
/// Measured on this project (see NOTES): exactly one timing arrives per frame with one
/// frame of lag, so per-frame attribution is exact and no segment-level fallback is needed.
/// The one-per-frame invariant is asserted anyway, because if it ever stops holding the
/// numbers would still look entirely plausible.
/// </summary>
public class FrameSampler : System.IDisposable
{
	public struct Sample
	{
		public double wallMs;
		public double cpuFrameMs, cpuMainMs, cpuRenderMs, cpuPresentWaitMs, gpuMs;
		public bool timingValid;

		public long drawCalls, batches, setPassCalls, triangles, vertices, shadowCasters;
		public long gcAllocBytes, totalUsedBytes, totalReservedBytes, gfxUsedBytes,
			systemUsedBytes, gcUsedBytes, gcReservedBytes;
	}

	static readonly (ProfilerCategory category, string name)[] CounterDefs =
	{
		(ProfilerCategory.Render, "Draw Calls Count"),
		(ProfilerCategory.Render, "Batches Count"),
		(ProfilerCategory.Render, "SetPass Calls Count"),
		(ProfilerCategory.Render, "Triangles Count"),
		(ProfilerCategory.Render, "Vertices Count"),
		(ProfilerCategory.Render, "Shadow Casters Count"),
		(ProfilerCategory.Memory, "GC Allocated In Frame"),
		(ProfilerCategory.Memory, "Total Used Memory"),
		(ProfilerCategory.Memory, "Total Reserved Memory"),
		(ProfilerCategory.Memory, "Gfx Used Memory"),
		(ProfilerCategory.Memory, "System Used Memory"),
		(ProfilerCategory.Memory, "GC Used Memory"),
		(ProfilerCategory.Memory, "GC Reserved Memory"),
	};

	const int TimingWindow = 16;

	readonly ProfilerRecorder[] recorders = new ProfilerRecorder[CounterDefs.Length];
	readonly FrameTiming[] timingBuffer = new FrameTiming[TimingWindow];
	readonly System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();

	long previousTicks;
	double lastSeenPresentTime = -1;
	int captureCount;

	public bool FrameTimingAvailable { get; private set; }
	public int TimingLagFrames { get; private set; } = -1;
	/// <summary>Frames where the one-timing-per-frame invariant did not hold.</summary>
	public int AttributionAnomalies { get; private set; }

	public FrameSampler()
	{
		for (int i = 0; i < CounterDefs.Length; i++)
		{
			recorders[i] = ProfilerRecorder.StartNew(CounterDefs[i].category, CounterDefs[i].name);
		}
		stopwatch.Start();
		previousTicks = stopwatch.ElapsedTicks;
	}

	/// <summary>Which counters this build actually exposes. Unity strips many of them
	/// from non-development players, and an absent counter must read as empty rather than
	/// as zero.</summary>
	public List<(string name, bool available)> CounterAvailability()
	{
		var list = new List<(string, bool)>(CounterDefs.Length);
		for (int i = 0; i < CounterDefs.Length; i++)
		{
			list.Add((CounterDefs[i].name, recorders[i].Valid));
		}
		return list;
	}

	public bool CounterValid(int index) => recorders[index].Valid;

	/// <summary>Takes a sample describing the frame that just completed.</summary>
	public Sample Capture()
	{
		var sample = new Sample();

		long ticks = stopwatch.ElapsedTicks;
		sample.wallMs = (ticks - previousTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
		previousTicks = ticks;

		FrameTimingManager.CaptureFrameTimings();
		captureCount++;

		uint count = FrameTimingManager.GetLatestTimings(TimingWindow, timingBuffer);
		int fresh = 0;
		if (count > 0)
		{
			// Buffer is newest-first; walk until we reach one already seen.
			for (int i = 0; i < count; i++)
			{
				if (lastSeenPresentTime >= 0 && timingBuffer[i].cpuTimePresentCalled <= lastSeenPresentTime)
				{
					break;
				}
				fresh++;
			}
		}

		if (fresh > 0)
		{
			lastSeenPresentTime = timingBuffer[0].cpuTimePresentCalled;

			// The newest is the frame just completed. If more than one arrived we are no
			// longer one-to-one; record it and take the newest.
			FrameTiming timing = timingBuffer[0];
			sample.cpuFrameMs = timing.cpuFrameTime;
			sample.cpuMainMs = timing.cpuMainThreadFrameTime;
			sample.cpuRenderMs = timing.cpuRenderThreadFrameTime;
			sample.cpuPresentWaitMs = timing.cpuMainThreadPresentWaitTime;
			sample.gpuMs = timing.gpuFrameTime;
			sample.timingValid = true;

			if (!FrameTimingAvailable && timing.gpuFrameTime > 0)
			{
				FrameTimingAvailable = true;
				TimingLagFrames = captureCount;
			}

			if (fresh > 1) { AttributionAnomalies++; }
		}
		else if (captureCount > 4)
		{
			// Past priming, a frame with no new timing breaks one-to-one attribution.
			AttributionAnomalies++;
		}

		ReadCounters(ref sample);
		return sample;
	}

	void ReadCounters(ref Sample sample)
	{
		sample.drawCalls = Read(0);
		sample.batches = Read(1);
		sample.setPassCalls = Read(2);
		sample.triangles = Read(3);
		sample.vertices = Read(4);
		sample.shadowCasters = Read(5);
		sample.gcAllocBytes = Read(6);
		sample.totalUsedBytes = Read(7);
		sample.totalReservedBytes = Read(8);
		sample.gfxUsedBytes = Read(9);
		sample.systemUsedBytes = Read(10);
		sample.gcUsedBytes = Read(11);
		sample.gcReservedBytes = Read(12);
	}

	// -1 means "counter not available", which the writer emits as an empty cell so it can
	// never be mistaken for a genuine zero.
	long Read(int index) => recorders[index].Valid ? recorders[index].LastValue : -1L;

	public void Dispose()
	{
		for (int i = 0; i < recorders.Length; i++)
		{
			if (recorders[i].Valid) { recorders[i].Dispose(); }
		}
		stopwatch.Stop();
	}
}
