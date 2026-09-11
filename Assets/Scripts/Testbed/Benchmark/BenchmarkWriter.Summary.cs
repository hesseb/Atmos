using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// See BenchmarkWriter.cs for the shadowing note this alias exists for.
using IOPath = System.IO.Path;

public static partial class BenchmarkWriter
{
	// -------------------------------------------------------------- summary.md

	static string BuildSummary( RunMetadata meta, List<PassResult> passes )
	{
		var sb = new StringBuilder();
		sb.Append( "# Benchmark run `" ).Append( meta.run_id ).Append( "`\n\n" );

		bool capture = meta.run_mode == BenchmarkRunMode.Capture.ToString();

		if ( capture )
		{
			sb.Append( "> **Capture run - no measurements.** This run exists to produce images.\n" )
			  .Append( "> Every screenshot forces a GPU-to-CPU readback that stalls the frame it\n" )
			  .Append( "> is taken on, so the frame times here are meaningless and every row is\n" )
			  .Append( "> marked `measured = 0`. The matching numbers come from a Timing run of\n" )
			  .Append( "> the same benchmark; pair them by frame index after checking that the\n" )
			  .Append( "> plan and pose hashes agree.\n\n" );
		}

		if ( meta.is_editor )
		{
			sb.Append( "> **Editor run - not authoritative.** Editor overhead and an unpinned Game\n" )
			  .Append( "> view size make these numbers indicative only. Use a standalone build for\n" )
			  .Append( "> anything that goes in the report.\n\n" );
		}

		if ( meta.git_dirty )
		{
			sb.Append( "> **Working tree was dirty.** These numbers were produced from uncommitted\n" )
			  .Append( "> code and are not reproducible from `" ).Append( meta.git_commit ).Append( "` alone.\n\n" );
		}

		if ( !meta.instrumentation.frame_timing_available )
		{
			sb.Append( "> **No GPU timing.** Frame Timing Stats is disabled in Player Settings, so\n" )
			  .Append( "> the GPU columns are empty and only CPU-side numbers are available.\n\n" );
		}

		sb.Append( "| | |\n|---|---|\n" )
		  .Append( "| benchmark | `" ).Append( meta.benchmark.id ).Append( "` |\n" )
		  .Append( "| plan hash | `" ).Append( meta.benchmark.plan_hash ).Append( "` |\n" )
		  .Append( "| pose hash | `" ).Append( meta.benchmark.pose_hash ).Append( "` |\n" )
		  .Append( "| mode | " ).Append( meta.run_mode ).Append( " |\n" )
		  .Append( "| frames | " ).Append( meta.benchmark.total_frames.ToString( Ci ) )
		  .Append( " total, " ).Append( meta.benchmark.measured_frames.ToString( Ci ) ).Append( " measured |\n" )
		  .Append( capture ? "| screenshots | " + meta.screenshots_captured.ToString( Ci ) + " |\n" : "" )
		  .Append( "| resolution | " ).Append( meta.resolution.actual_width.ToString( Ci ) ).Append( 'x' )
		  .Append( meta.resolution.actual_height.ToString( Ci ) )
		  .Append( meta.resolution.matched ? "" : " (**requested " + meta.resolution.requested_width + "x"
			  + meta.resolution.requested_height + "**)" ).Append( " |\n" )
		  .Append( "| MSAA | " ).Append( meta.quality.anti_aliasing.ToString( Ci ) ).Append( "x |\n" )
		  .Append( "| GPU | " ).Append( meta.hardware.graphics_device ).Append( " (" )
		  .Append( meta.hardware.graphics_api ).Append( ") |\n" )
		  .Append( "| Unity | " ).Append( meta.unity_version ).Append( " |\n" )
		  .Append( "| commit | `" ).Append( meta.git_commit ).Append( meta.git_dirty ? " (dirty)" : "" ).Append( "` |\n\n" );

		// A capture run has no segment statistics at all, so the timing tables would render
		// as bare headers. Point at the images instead.
		if ( capture )
		{
			sb.Append( "## Screenshots\n\n" )
			  .Append( "`screenshots/manifest.csv` lists every image with the frame index, " )
			  .Append( "segment, camera pose, sun elevation and sky fraction that produced it. " )
			  .Append( "Filenames are `f<frame>_<pass>_<segment>.png`, so the same frame across " )
			  .Append( "two renderer profiles differs only in the pass component.\n\n" );

			AppendReproducibility( sb, passes );
			AppendCaveats( sb, meta );
			return sb.ToString();
		}

		// --- per pass, per segment ---
		sb.Append( "## GPU frame time (ms)\n\n" )
		  .Append( "The 1% low is the **mean of the slowest 1% of frames**; p99 is the same tail " )
		  .Append( "expressed as a percentile. They can differ substantially - that is why both " )
		  .Append( "are reported.\n\n" );

		sb.Append( "| pass | segment | frames | sky frac | median | mean | p95 | p99 | 1% low | max |\n" )
		  .Append( "|---|---|---|---|---|---|---|---|---|---|\n" );

		foreach ( PassResult pass in passes )
		{
			foreach ( SegmentStats s in pass.segments )
			{
				sb.Append( "| " ).Append( pass.passId )
				  .Append( " | " ).Append( s.label )
				  .Append( " | " ).Append( s.frames.ToString( Ci ) )
				  .Append( " | " ).Append( s.meanSkyFraction.ToString( "F2", Ci ) )
				  .Append( " | " ).Append( M( s.gpu.medianMs ) )
				  .Append( " | " ).Append( M( s.gpu.meanMs ) )
				  .Append( " | " ).Append( M( s.gpu.p95Ms ) )
				  .Append( " | " ).Append( M( s.gpu.p99Ms ) )
				  .Append( " | " ).Append( M( s.gpu.p1LowMeanMs ) )
				  .Append( " | " ).Append( M( s.gpu.maxMs ) )
				  .Append( " |\n" );
			}
		}

		sb.Append( "\n## CPU frame time (ms)\n\n" )
		  .Append( "| pass | segment | median | mean | p99 | avg fps |\n|---|---|---|---|---|---|\n" );
		foreach ( PassResult pass in passes )
		{
			foreach ( SegmentStats s in pass.segments )
			{
				sb.Append( "| " ).Append( pass.passId )
				  .Append( " | " ).Append( s.label )
				  .Append( " | " ).Append( M( s.cpu.medianMs ) )
				  .Append( " | " ).Append( M( s.cpu.meanMs ) )
				  .Append( " | " ).Append( M( s.cpu.p99Ms ) )
				  .Append( " | " ).Append( M( s.cpu.avgFps ) )
				  .Append( " |\n" );
			}
		}

		AppendComparison( sb, passes );
		AppendReproducibility( sb, passes );

		if ( meta.run_mode == BenchmarkRunMode.SelfCheck.ToString() )
		{
			sb.Append( "\nSee `selfcheck.md` for the run-to-run spread these repeats disagreed " )
			  .Append( "by - the margin any delta above has to clear to mean anything.\n" );
		}

		AppendCaveats( sb, meta );

		return sb.ToString();
	}

	static void AppendCaveats( StringBuilder sb, RunMetadata meta )
	{
		if ( meta.warnings == null || meta.warnings.Length == 0 ) { return; }

		sb.Append( "\n## Caveats\n\n" );
		foreach ( string w in meta.warnings ) { sb.Append( "- `" ).Append( w ).Append( "`\n" ); }
	}

	/// <summary>
	/// Profile-vs-profile deltas per segment, using the median across repeats so a single
	/// anomalous repeat cannot drive the headline number.
	/// </summary>
	static void AppendComparison( StringBuilder sb, List<PassResult> passes )
	{
		var profiles = new List<string>();
		foreach ( PassResult p in passes )
		{
			if ( !string.IsNullOrEmpty( p.profileId ) && !profiles.Contains( p.profileId ) )
			{
				profiles.Add( p.profileId );
			}
		}
		if ( profiles.Count < 2 ) { return; }

		string baseline = profiles[ 0 ];
		sb.Append( "\n## Comparison (GPU median, ms)\n\n" )
		  .Append( "Median across repeats, per segment. Baseline is `" ).Append( baseline ).Append( "`.\n\n" );

		sb.Append( "| segment |" );
		foreach ( string p in profiles ) { sb.Append( ' ' ).Append( p ).Append( " |" ); }
		for ( int i = 1; i < profiles.Count; i++ )
		{
			sb.Append( ' ' ).Append( profiles[ i ] ).Append( " - " ).Append( baseline ).Append( " |" );
		}
		sb.Append( '\n' ).Append( "|---|" );
		for ( int i = 0; i < profiles.Count + profiles.Count - 1; i++ ) { sb.Append( "---|" ); }
		sb.Append( '\n' );

		// Segment labels are shared across passes because the plan is shared.
		var labels = new List<string>();
		foreach ( SegmentStats s in passes[ 0 ].segments )
		{
			if ( !labels.Contains( s.label ) ) { labels.Add( s.label ); }
		}

		foreach ( string label in labels )
		{
			sb.Append( "| " ).Append( label ).Append( " |" );

			var medians = new List<double>();
			foreach ( string profile in profiles )
			{
				double m = MedianOfRepeats( passes, profile, label );
				medians.Add( m );
				sb.Append( ' ' ).Append( M( m ) ).Append( " |" );
			}

			for ( int i = 1; i < medians.Count; i++ )
			{
				double delta = medians[ i ] - medians[ 0 ];
				string sign = delta >= 0 ? "+" : "";
				sb.Append( ' ' ).Append( sign ).Append( M( delta ) ).Append( " |" );
			}
			sb.Append( '\n' );
		}
	}

	static double MedianOfRepeats( List<PassResult> passes, string profileId, string label )
	{
		var values = new List<double>();
		foreach ( PassResult p in passes )
		{
			if ( p.profileId != profileId ) { continue; }
			foreach ( SegmentStats s in p.segments )
			{
				if ( s.label == label && s.gpu.Valid ) { values.Add( s.gpu.medianMs ); }
			}
		}

		if ( values.Count == 0 ) { return double.NaN; }
		values.Sort();
		return BenchmarkStats.Percentile( values.ToArray(), 0.5 );
	}

	/// <summary>
	/// Hash agreement and run-to-run spread.
	///
	/// The spread is the measurement noise floor: if a profile-vs-profile delta is not
	/// comfortably larger than it, the result is not significant. Better to learn that
	/// here than from an examiner.
	/// </summary>
	static void AppendReproducibility( StringBuilder sb, List<PassResult> passes )
	{
		sb.Append( "\n## Reproducibility\n\n" );

		bool posesAgree = true;
		for ( int i = 1; i < passes.Count; i++ )
		{
			if ( passes[ i ].poseHash != passes[ 0 ].poseHash ) { posesAgree = false; break; }
		}

		sb.Append( posesAgree
			? "- **Pose hash identical across every pass** - all passes rendered the same poses.\n"
			: "- **POSE HASH MISMATCH** - passes did not render the same poses. The comparison " +
			  "is invalid; something perturbed the camera or the sun.\n" );

		sb.Append( "\n| pass | profile | repeat | pose hash | scene hash |\n|---|---|---|---|---|\n" );
		foreach ( PassResult p in passes )
		{
			sb.Append( "| " ).Append( p.passId )
			  .Append( " | " ).Append( p.profileId )
			  .Append( " | " ).Append( p.repeat.ToString( Ci ) )
			  .Append( " | `0x" ).Append( p.poseHash.ToString( "x16" ) )
			  .Append( "` | `0x" ).Append( p.sceneHash.ToString( "x16" ) ).Append( "` |\n" );
		}

		sb.Append( "\nScene hash covers draw calls, triangles and LOD state. It should match " )
		  .Append( "between repeats of one profile, and may legitimately differ between " )
		  .Append( "profiles by the passes each adds.\n" );

		if ( !CountersPresent( passes ) )
		{
			sb.Append( "\n> **Scene hash is degraded here.** Draw calls and triangles read zero " )
			  .Append( "on every frame - the Render profiler counters were stripped, which is a " )
			  .Append( "release player. Only the LOD count still contributes, so two profiles " )
			  .Append( "drawing genuinely different geometry can share a scene hash. Do not read " )
			  .Append( "a match as evidence they drew the same thing.\n" );
		}
	}

	/// <summary>
	/// Whether the Render profiler counters were live. A release player strips them, and
	/// every counter then reads zero - which several checks would otherwise interpret as
	/// agreement rather than as absence.
	/// </summary>
	static bool CountersPresent( List<PassResult> passes )
	{
		foreach ( PassResult pass in passes )
		{
			if ( pass.geometry == null ) { continue; }
			foreach ( FrameGeometry g in pass.geometry )
			{
				if ( g.drawCalls > 0 ) { return true; }
			}
		}
		return false;
	}

	static string M( double v ) => double.IsNaN( v ) ? "n/a" : v.ToString( "F3", Ci );
}
