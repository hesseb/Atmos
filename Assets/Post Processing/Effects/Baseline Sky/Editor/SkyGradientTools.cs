using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Generates a starting-point sky gradient for the baseline renderer.
///
/// This is a *hand-authored* baseline, so the output is meant to be edited afterwards - the
/// point of the gradient-LUT approach is that an artist can open it in an image editor and
/// change the sky without touching a shader. That authoring flexibility is one of the four
/// axes the thesis compares, so the workflow matters as much as the result. What it produces
/// is a plausible clear-sky model built from named anchor colours, not a simulation -
/// anything derived from the scattering maths belongs in the baked variant, which is a
/// deliberately separate technique.
///
/// **The anchors are authored as the colour you want on screen, and the tone map is inverted
/// to find the value to store.** Authoring the stored values directly does not work: the
/// shared tone map applies `lerp(0.5, lum, contrast)` with contrast 1.45, which has a
/// pedestal at 0.155 - every plausible night-sky radiance below that lands on the smoothMax
/// floor and comes out flat black, leaving the stars nothing to sit against, while daylight
/// values clip past 1. The usable input band turns out to be roughly [0.17, 0.87], which is
/// not a range anyone would guess.
/// </summary>
static class SkyGradientTools
{
	const string Folder = "Assets/Post Processing/Effects/Baseline Sky/Textures";
	const string GradientPath = Folder + "/SkyGradient.exr";

	// U axis: view elevation. 0 = straight down, 0.5 = horizon, 1 = zenith.
	const int Width = 128;
	// V axis: sun elevation, -90 to +90 degrees.
	const int Height = 64;

	/// <summary>Anchor colours at one sun elevation, as they should appear on screen.</summary>
	struct SkyAnchor
	{
		public float sunElevationDegrees;
		public Color horizon;
		public Color zenith;
	}

	// These are the numbers to change when hand-tuning. Displayed colour, 0..1 - the project
	// renders in Gamma colour space, so what is written here is what appears.
	static readonly SkyAnchor[] Anchors =
	{
		new SkyAnchor { sunElevationDegrees = -90f, horizon = new Color(0.035f, 0.045f, 0.075f), zenith = new Color(0.015f, 0.020f, 0.040f) },
		new SkyAnchor { sunElevationDegrees = -12f, horizon = new Color(0.100f, 0.110f, 0.170f), zenith = new Color(0.030f, 0.040f, 0.080f) },
		new SkyAnchor { sunElevationDegrees =  -4f, horizon = new Color(0.380f, 0.240f, 0.220f), zenith = new Color(0.070f, 0.100f, 0.190f) },
		new SkyAnchor { sunElevationDegrees =   0f, horizon = new Color(0.950f, 0.470f, 0.220f), zenith = new Color(0.160f, 0.240f, 0.450f) },
		new SkyAnchor { sunElevationDegrees =  10f, horizon = new Color(0.920f, 0.740f, 0.560f), zenith = new Color(0.280f, 0.450f, 0.750f) },
		new SkyAnchor { sunElevationDegrees =  30f, horizon = new Color(0.720f, 0.820f, 0.940f), zenith = new Color(0.240f, 0.460f, 0.860f) },
		new SkyAnchor { sunElevationDegrees =  90f, horizon = new Color(0.700f, 0.810f, 0.950f), zenith = new Color(0.220f, 0.440f, 0.880f) }
	};

	// The sky brightens quickly just above the horizon and then flattens out. A straight lerp
	// horizon-to-zenith looks conspicuously wrong, and this is the cheapest fix.
	const float ZenithFalloff = 0.65f;

	[MenuItem("Testbed/Baseline Sky/Generate Default Sky Gradient")]
	static void Generate()
	{
		if (File.Exists(GradientPath))
		{
			bool overwrite = EditorUtility.DisplayDialog("Overwrite sky gradient?",
				$"{GradientPath} already exists.\n\nIf it has been hand-tuned, regenerating " +
				"discards that work.", "Overwrite", "Cancel");
			if (!overwrite) { return; }
		}

		// The inverse has to use the same tone-map constants the renderer will apply, or the
		// result is wrong by exactly the amount they differ. Read them from the scene rather
		// than assuming.
		var renderer = Object.FindFirstObjectByType<BaselineSkyRenderer>(FindObjectsInactive.Include);
		float intensity = renderer != null ? renderer.intensity : 1f;
		float contrast = renderer != null ? renderer.contrast : 1.45f;
		float whitePoint = renderer != null ? renderer.whitePoint : 1.1f;

		if (renderer == null)
		{
			Debug.LogWarning("[BaselineSky] no BaselineSkyRenderer in the open scene; using " +
				"default tone-map constants. If the renderer's differ, regenerate afterwards.");
		}

		Directory.CreateDirectory(Folder);

		var texture = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, linear: true);
		var pixels = new Color[Width * Height];

		for (int y = 0; y < Height; y++)
		{
			float sunElevation01 = Height == 1 ? 0.5f : y / (float)(Height - 1);
			float sunElevationDegrees = (sunElevation01 * 2f - 1f) * 90f;

			Sample(sunElevationDegrees, out Color horizon, out Color zenith);

			for (int x = 0; x < Width; x++)
			{
				float viewElevation01 = Width == 1 ? 0.5f : x / (float)(Width - 1);

				// Interpolate in display space, then invert once. Interpolating the stored
				// values instead would make the midpoint of a gradient not look like the
				// midpoint, since the tone map is non-linear.
				Color displayed = Shade(viewElevation01, horizon, zenith);
				pixels[y * Width + x] = InverseToneMap(displayed, intensity, contrast, whitePoint);
			}
		}

		texture.SetPixels(pixels);
		texture.Apply();

		File.WriteAllBytes(GradientPath, texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat));
		Object.DestroyImmediate(texture);

		AssetDatabase.ImportAsset(GradientPath, ImportAssetOptions.ForceUpdate);
		ConfigureImporter(GradientPath);

		// This gradient is inverse-tone-mapped, so it is the tone-map constants it goes stale
		// against - not the atmosphere.
		SkyBakeStampWriter.Record(GradientPath, SkyBakeStamp.Recipe.ToneMap, null, renderer,
			new SkyBakeStamp.Inputs().Add("bakeWidth", Width).Add("bakeHeight", Height)
				.Add("bakeZenithFalloff", ZenithFalloff));

		Debug.Log($"[BaselineSky] wrote {Width}x{Height} sky gradient to {GradientPath}\n" +
			"  U = view elevation (0 down, 0.5 horizon, 1 zenith), V = sun elevation (-90 to +90)\n" +
			$"  Linear EXR, pre-tone-map. Inverted against intensity={intensity}, " +
			$"contrast={contrast}, whitePoint={whitePoint}.\n" +
			"  Changing those on the renderer means regenerating or the sky shifts.");

		Selection.activeObject = AssetDatabase.LoadAssetAtPath<Texture2D>(GradientPath);
	}

	/// <summary>Interpolates the anchor set at a given sun elevation.</summary>
	static void Sample(float sunElevationDegrees, out Color horizon, out Color zenith)
	{
		if (sunElevationDegrees <= Anchors[0].sunElevationDegrees)
		{
			horizon = Anchors[0].horizon;
			zenith = Anchors[0].zenith;
			return;
		}

		for (int i = 1; i < Anchors.Length; i++)
		{
			if (sunElevationDegrees > Anchors[i].sunElevationDegrees) { continue; }

			SkyAnchor a = Anchors[i - 1];
			SkyAnchor b = Anchors[i];
			float span = b.sunElevationDegrees - a.sunElevationDegrees;
			float t = span > 0f ? (sunElevationDegrees - a.sunElevationDegrees) / span : 0f;

			// Smoothstep rather than linear: linear interpolation between anchors produces a
			// visible crease as the sun crosses one, which is exactly when someone is looking.
			t = t * t * (3f - 2f * t);

			horizon = Color.Lerp(a.horizon, b.horizon, t);
			zenith = Color.Lerp(a.zenith, b.zenith, t);
			return;
		}

		horizon = Anchors[Anchors.Length - 1].horizon;
		zenith = Anchors[Anchors.Length - 1].zenith;
	}

	static Color Shade(float viewElevation01, Color horizon, Color zenith)
	{
		if (viewElevation01 >= 0.5f)
		{
			float t = Mathf.Pow((viewElevation01 - 0.5f) * 2f, ZenithFalloff);
			return Color.Lerp(horizon, zenith, t);
		}

		// Below the horizon. Terrain covers this from the ground, but this is a globe scene -
		// from orbit the camera looks down past the limb and needs something there.
		float below = viewElevation01 * 2f;
		return horizon * Mathf.Lerp(0.25f, 1f, below);
	}

	// ------------------------------------------------------------------ tone map inverse

	/// <summary>
	/// The exact inverse of toneMap() in DrawAtmosphereCommon.hlsl, minus the smoothMax floor
	/// (which is identity above zero and only soft-clamps negatives).
	/// </summary>
	static Color InverseToneMap(Color displayed, float intensity, float contrast, float whitePoint)
	{
		return new Color(
			InverseToneMapChannel(displayed.r, intensity, contrast, whitePoint),
			InverseToneMapChannel(displayed.g, intensity, contrast, whitePoint),
			InverseToneMapChannel(displayed.b, intensity, contrast, whitePoint),
			1f);
	}

	static float InverseToneMapChannel(float displayed, float intensity, float contrast, float whitePoint)
	{
		displayed = Mathf.Max(0f, displayed);

		// reinhard_extended is v * (1 + v/W^2) / (1 + v). Setting that equal to the displayed
		// value and rearranging gives v^2 + W^2*(1-d)*v - W^2*d = 0; take the positive root.
		// It is monotonic and unbounded, so every non-negative target has exactly one answer.
		float w2 = whitePoint * whitePoint;
		float b = w2 * (1f - displayed);
		float v = (-b + Mathf.Sqrt(b * b + 4f * w2 * displayed)) * 0.5f;

		// Then undo the contrast pedestal and the intensity scale.
		return (0.5f + (v - 0.5f) / Mathf.Max(1e-4f, contrast)) / Mathf.Max(1e-4f, intensity);
	}

	static void ConfigureImporter(string path)
	{
		var importer = AssetImporter.GetAtPath(path) as TextureImporter;
		if (importer == null) { return; }

		// sRGB off: the values are pre-tone-map radiance, not display colour.
		importer.sRGBTexture = false;
		importer.mipmapEnabled = false;
		// Clamp, or the horizon wraps to the zenith at the texture edges.
		importer.wrapMode = TextureWrapMode.Clamp;
		importer.filterMode = FilterMode.Bilinear;
		// Uncompressed: a 128x64 texture whose whole job is smooth gradients, and block
		// compression puts visible blotches in exactly that.
		importer.textureCompression = TextureImporterCompression.Uncompressed;
		importer.SaveAndReimport();
	}
}
