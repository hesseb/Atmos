using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// See BenchmarkWriter.cs for the shadowing note this alias exists for.
using IOPath = System.IO.Path;

public static partial class BenchmarkWriter
{
	// -------------------------------------------------------------- frames.csv

	static string BuildFramesCsv(BenchmarkRunner runner)
	{
		BenchmarkPlan plan = runner.Plan;
		int count = Mathf.Min(runner.FrameCursor, runner.Records.Length);
		var sb = new StringBuilder(count * 320);

		// A capture run's frame times carry the screenshot readback stalls, so no row from it
		// is measured. The rows are still written - the pose columns are the evidence that
		// the captured images correspond to the timing run's frames.
		bool measuredRun = !runner.IsCaptureRun;

		sb.Append("frame_index,phase,segment_index,segment_label,segment_frame,measured,")
		  .Append("sim_time_s,wall_ms,delta_ms,")
		  .Append("cpu_frame_ms,cpu_main_ms,cpu_render_ms,cpu_present_wait_ms,gpu_ms,timing_valid,")
		  .Append("draw_calls,batches,setpass_calls,triangles,vertices,shadow_casters,")
		  .Append("gc_alloc_bytes,mem_total_used,mem_total_reserved,mem_gfx_used,mem_system_used,mem_gc_used,")
		  .Append("plan_lon,plan_lat,plan_alt,plan_pitch,plan_heading,plan_roll,plan_fov,")
		  .Append("cam_pos_x,cam_pos_y,cam_pos_z,cam_fwd_x,cam_fwd_y,cam_fwd_z,")
		  .Append("sun_dayT,sun_monthT,sun_yearT,sun_dir_x,sun_dir_y,sun_dir_z,")
		  .Append("lod_high_res,sky_fraction,screenshot\n");

		float dt = plan.CaptureDeltaTime;

		for (int i = 0; i < count; i++)
		{
			PlannedFrame p = plan.frames[i];
			BenchmarkRunner.FrameRecord r = runner.Records[i];
			FrameSampler.Sample s = r.sample;

			string label = p.segmentIndex >= 0 && p.segmentIndex < plan.segmentLabels.Length
				? plan.segmentLabels[p.segmentIndex] : "";

			sb.Append(i.ToString(Ci)).Append(',')
			  .Append(p.phase).Append(',')
			  .Append(p.segmentIndex.ToString(Ci)).Append(',')
			  .Append(label).Append(',')
			  .Append(p.segmentFrame.ToString(Ci)).Append(',')
			  .Append(measuredRun && p.phase == BenchmarkPhase.Measure ? '1' : '0').Append(',')
			  .Append(F(i * dt)).Append(',')
			  .Append(F(s.wallMs)).Append(',')
			  .Append(F(r.deltaMs)).Append(',')
			  .Append(F(s.cpuFrameMs)).Append(',')
			  .Append(F(s.cpuMainMs)).Append(',')
			  .Append(F(s.cpuRenderMs)).Append(',')
			  .Append(F(s.cpuPresentWaitMs)).Append(',')
			  .Append(F(s.gpuMs)).Append(',')
			  .Append(s.timingValid ? '1' : '0').Append(',')
			  .Append(L(s.drawCalls)).Append(',')
			  .Append(L(s.batches)).Append(',')
			  .Append(L(s.setPassCalls)).Append(',')
			  .Append(L(s.triangles)).Append(',')
			  .Append(L(s.vertices)).Append(',')
			  .Append(L(s.shadowCasters)).Append(',')
			  .Append(L(s.gcAllocBytes)).Append(',')
			  .Append(L(s.totalUsedBytes)).Append(',')
			  .Append(L(s.totalReservedBytes)).Append(',')
			  .Append(L(s.gfxUsedBytes)).Append(',')
			  .Append(L(s.systemUsedBytes)).Append(',')
			  .Append(L(s.gcUsedBytes)).Append(',')
			  .Append(F(p.view.coordinate.longitude)).Append(',')
			  .Append(F(p.view.coordinate.latitude)).Append(',')
			  .Append(F(p.view.altitude)).Append(',')
			  .Append(F(p.view.pitch)).Append(',')
			  .Append(F(p.view.heading)).Append(',')
			  .Append(F(p.view.roll)).Append(',')
			  .Append(F(p.view.fieldOfView)).Append(',')
			  .Append(F(r.cameraPosition.x)).Append(',')
			  .Append(F(r.cameraPosition.y)).Append(',')
			  .Append(F(r.cameraPosition.z)).Append(',')
			  .Append(F(r.cameraForward.x)).Append(',')
			  .Append(F(r.cameraForward.y)).Append(',')
			  .Append(F(r.cameraForward.z)).Append(',')
			  .Append(F(p.dayT)).Append(',')
			  .Append(F(p.monthT)).Append(',')
			  .Append(F(p.yearT)).Append(',')
			  .Append(F(r.sunDirection.x)).Append(',')
			  .Append(F(r.sunDirection.y)).Append(',')
			  .Append(F(r.sunDirection.z)).Append(',')
			  .Append(r.lodHighResCount.ToString(Ci)).Append(',')
			  .Append(F(p.skyFraction)).Append(',')
			  .Append(p.screenshot ? '1' : '0').Append('\n');
		}

		return sb.ToString();
	}

	static string F(double v) => v.ToString("G9", Ci);
	static string F(float v) => v.ToString("G9", Ci);
	// -1 marks an unavailable counter; emitted as an empty cell so it can never be read
	// as a genuine zero.
	static string L(long v) => v < 0 ? "" : v.ToString(Ci);

}
