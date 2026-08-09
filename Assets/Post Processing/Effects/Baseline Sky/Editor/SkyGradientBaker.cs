using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bakes the physically based sky into the baseline's gradient LUT.
///
/// This is the third baseline variant, and it answers a different question from the
/// hand-authored one. The hand-authored gradient asks "how does a cheap sky someone painted
/// compare to a simulated one?"; the baked gradient asks "how much of the simulated sky's
/// appearance survives being flattened into a texture lookup, and what does that cost?" The
/// second is the more interesting RQ3 question, because the answer is a concrete
/// optimization rather than a value judgement.
///
/// Output is interchangeable with the hand-authored gradient - same layout, same shader, same
/// pre-tone-map linear values - so switching between them is a texture swap.
/// </summary>
static class SkyGradientBaker
{
	const string Folder = "Assets/Post Processing/Effects/Baseline Sky/Textures";
	const string BakedPath = Folder + "/SkyGradientBaked.exr";
	const string ComputePath = "Assets/Post Processing/Effects/Baseline Sky/BakeSkyGradient.compute";

	// Matches the hand-authored gradient so the two are drop-in replacements for each other.
	const int Width = 128;
	const int Height = 64;

	// Offline, so there is no reason to be stingy. Well above the runtime sky's 256 steps.
	const int ScatteringSteps = 512;

	/// <summary>
	/// Horizontal direction the gradient is sampled along, measured from the sun.
	///
	/// 0 looks toward the sun. Averaging over azimuth instead was the first attempt and it
	/// removed sunsets entirely: at low sun the sky is bright only sunward, the mean falls
	/// below the tone map's pedestal at 0.155, and everything under that crushes to black. The
	/// cost of sampling sunward is that the anti-solar sky comes out too warm - a limitation
	/// of having no azimuth axis at all, not of this number.
	/// </summary>
	const float BakeAzimuthDegrees = 0f;

	/// <summary>
	/// Observer height above the surface, in world units. The LUT has no altitude axis, so
	/// this single choice applies everywhere.
	///
	/// Set to match the altitude the benchmarks actually fly at - `daycycle` sits at 12 and
	/// `orbit` at 30 - rather than to ground level, because a strategy-game camera is never
	/// at ground level and baking there would make the baseline wrong in the common case
	/// while being right in a case that never occurs.
	/// </summary>
	const float BakeAltitude = 12f;

	[MenuItem("Testbed/Baseline Sky/Bake Sky Gradient From PBR")]
	static void Bake()
	{
		AtmosphereEffect atmosphere = FindAtmosphere();
		if (atmosphere == null)
		{
			Debug.LogError("[BaselineSky] no AtmosphereEffect asset found - nothing to bake from.");
			return;
		}

		var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
		if (compute == null)
		{
			Debug.LogError($"[BaselineSky] missing compute shader at {ComputePath}.");
			return;
		}

		// Applies the atmosphere's own parameters and guarantees its transmittance LUT has
		// been rendered. The bake is only meaningful because it shares this state with the
		// runtime renderer rather than restating it.
		atmosphere.ApplyAtmosphereValuesTo(compute);

		if (atmosphere.transmittanceLUT == null)
		{
			Debug.LogError("[BaselineSky] the atmosphere's transmittance LUT is null after " +
				"initialisation - open the scene containing the atmosphere and try again.");
			return;
		}

		var target = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBFloat)
		{
			enableRandomWrite = true,
			name = "Sky Gradient Bake"
		};
		target.Create();

		int kernel = compute.FindKernel("BakeSkyGradientRow");
		compute.SetTexture(kernel, "Result", target);
		compute.SetTexture(kernel, "TransmittanceLUT", atmosphere.transmittanceLUT);
		compute.SetInt("width", Width);
		compute.SetInt("numScatteringSteps", ScatteringSteps);
		compute.SetFloat("bakeAzimuthDegrees", BakeAzimuthDegrees);
		compute.SetFloat("bakeAltitude", BakeAltitude);

		// An arbitrary but fixed observer frame. Only the angle between the view and the sun
		// matters to the scattering, so the absolute orientation is irrelevant as long as it
		// is consistent across rows.
		Vector3 up = Vector3.up;
		Vector3 east = Vector3.right;
		compute.SetVector("observerUp", up);
		compute.SetVector("observerEast", east);

		int groups = Mathf.CeilToInt(Width / 64f);

		try
		{
			for (int row = 0; row < Height; row++)
			{
				if (EditorUtility.DisplayCancelableProgressBar("Baking sky gradient",
					$"sun elevation row {row + 1} / {Height}", (row + 1) / (float)Height))
				{
					Debug.LogWarning("[BaselineSky] bake cancelled; no asset written.");
					return;
				}

				float sunElevation01 = Height <= 1 ? 0.5f : row / (float)(Height - 1);
				float radians = (sunElevation01 * 2f - 1f) * Mathf.PI * 0.5f;
				Vector3 dirToSun = up * Mathf.Sin(radians) + east * Mathf.Cos(radians);

				// Set per dispatch: dirToSun is a global inside AtmosphereCommon.hlsl, so one
				// dispatch can only cover a single sun elevation.
				compute.SetVector("dirToSun", dirToSun.normalized);
				compute.SetInt("row", row);
				compute.Dispatch(kernel, groups, 1, 1);
			}
		}
		finally
		{
			EditorUtility.ClearProgressBar();
		}

		WriteAsset(target);

		target.Release();
		Object.DestroyImmediate(target);
	}

	static void WriteAsset(RenderTexture source)
	{
		Directory.CreateDirectory(Folder);

		var texture = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, linear: true);
		RenderTexture previous = RenderTexture.active;
		RenderTexture.active = source;
		texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
		texture.Apply();
		RenderTexture.active = previous;

		// Report the range: if the whole LUT sits outside the tone map's usable band the
		// result will be flat black or clipped white, and the numbers say so immediately.
		Color[] pixels = texture.GetPixels();
		float min = float.MaxValue, max = float.MinValue;
		foreach (Color c in pixels)
		{
			min = Mathf.Min(min, Mathf.Min(c.r, Mathf.Min(c.g, c.b)));
			max = Mathf.Max(max, Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
		}

		File.WriteAllBytes(BakedPath, texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat));
		Object.DestroyImmediate(texture);

		AssetDatabase.ImportAsset(BakedPath, ImportAssetOptions.ForceUpdate);
		ConfigureImporter(BakedPath);

		Debug.Log($"[BaselineSky] baked {Width}x{Height} sky gradient to {BakedPath}\n" +
			$"  {ScatteringSteps} scattering steps, sampled {BakeAzimuthDegrees} deg from the sun, " +
			$"observer {BakeAltitude} above the surface\n" +
			$"  radiance range {min:F4} .. {max:F4}\n" +
			"  Azimuth is averaged out, so the Mie forward lobe is missing by construction - " +
			"the shader's glow term is what stands in for it.");

		Selection.activeObject = AssetDatabase.LoadAssetAtPath<Texture2D>(BakedPath);
	}

	// ------------------------------------------------------------------ cubemap

	const string CubemapPath = Folder + "/SkyCubemap.asset";
	// 512 rather than 256: a cubemap is magnified across the whole sky, and the horizon band
	// is where the gradient is steepest. 6 faces of RGBAHalf is about 12 MB, which is nothing
	// against the 900 MB already in Assets/Data.
	const int CubemapSize = 512;

	/// <summary>
	/// The sun elevation the cubemap is frozen at. A cubemap cannot track the sun - that is
	/// the whole point of the variant - so this is the single moment it is correct for.
	/// Chosen as mid-morning rather than noon or sunset: it makes the failure visible in both
	/// directions as the day cycle runs past it.
	/// </summary>
	const float CubemapSunElevationDegrees = 25f;

	/// <summary>
	/// Face bases, expressed so that the compute's v runs **downward** across the image, which
	/// is the Direct3D cube map convention:
	///   +X (1,-v,-u)   -X (-1,-v,u)   +Y (u,1,v)   -Y (u,-1,-v)   +Z (u,-v,1)   -Z (-u,-v,-1)
	/// Order matches UnityEngine.CubemapFace.
	/// </summary>
	static readonly Vector3[][] FaceBasis =
	{
		new[] { new Vector3(1, 0, 0),  new Vector3(0, 0, -1), new Vector3(0, -1, 0) },  // +X
		new[] { new Vector3(-1, 0, 0), new Vector3(0, 0, 1),  new Vector3(0, -1, 0) },  // -X
		new[] { new Vector3(0, 1, 0),  new Vector3(1, 0, 0),  new Vector3(0, 0, 1) },   // +Y
		new[] { new Vector3(0, -1, 0), new Vector3(1, 0, 0),  new Vector3(0, 0, -1) },  // -Y
		new[] { new Vector3(0, 0, 1),  new Vector3(1, 0, 0),  new Vector3(0, -1, 0) },  // +Z
		new[] { new Vector3(0, 0, -1), new Vector3(-1, 0, 0), new Vector3(0, -1, 0) }   // -Z
	};

	[MenuItem("Testbed/Baseline Sky/Bake Sky Cubemap From PBR")]
	static void BakeCubemap()
	{
		AtmosphereEffect atmosphere = FindAtmosphere();
		var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);

		if (atmosphere == null || compute == null)
		{
			Debug.LogError("[BaselineSky] need both an AtmosphereEffect asset and " +
				$"{ComputePath} to bake.");
			return;
		}

		atmosphere.ApplyAtmosphereValuesTo(compute);
		if (atmosphere.transmittanceLUT == null)
		{
			Debug.LogError("[BaselineSky] the atmosphere's transmittance LUT is null - open " +
				"the scene containing the atmosphere and try again.");
			return;
		}

		int kernel = compute.FindKernel("BakeSkyCubemapFace");

		var face = new RenderTexture(CubemapSize, CubemapSize, 0, RenderTextureFormat.ARGBFloat)
		{
			enableRandomWrite = true,
			name = "Sky Cubemap Face"
		};
		face.Create();

		compute.SetTexture(kernel, "FaceResult", face);
		compute.SetTexture(kernel, "TransmittanceLUT", atmosphere.transmittanceLUT);
		compute.SetInt("faceSize", CubemapSize);
		compute.SetInt("numScatteringSteps", ScatteringSteps);
		compute.SetFloat("bakeAltitude", BakeAltitude);

		Vector3 up = Vector3.up;
		Vector3 east = Vector3.right;
		compute.SetVector("observerUp", up);
		// Needed as the degenerate fallback in clampToHorizon, for rays pointing straight down.
		compute.SetVector("observerEast", east);

		float radians = CubemapSunElevationDegrees * Mathf.Deg2Rad;
		compute.SetVector("dirToSun", (up * Mathf.Sin(radians) + east * Mathf.Cos(radians)).normalized);

		int groups = Mathf.CeilToInt(CubemapSize / 8f);
		var pixels = new Color[6][];

		try
		{
			for (int f = 0; f < 6; f++)
			{
				EditorUtility.DisplayProgressBar("Baking sky cubemap",
					$"face {(CubemapFace)f}", (f + 1) / 6f);

				compute.SetVector("faceForward", FaceBasis[f][0]);
				compute.SetVector("faceRight", FaceBasis[f][1]);
				compute.SetVector("faceUp", FaceBasis[f][2]);
				compute.Dispatch(kernel, groups, groups, 1);

				pixels[f] = ReadBack(face, CubemapSize);
			}
		}
		finally
		{
			EditorUtility.ClearProgressBar();
		}

		face.Release();
		Object.DestroyImmediate(face);

		WriteCubemap(pixels);
	}

	static Color[] ReadBack(RenderTexture source, int size)
	{
		var texture = new Texture2D(size, size, TextureFormat.RGBAFloat, false, linear: true);
		RenderTexture previous = RenderTexture.active;
		RenderTexture.active = source;
		texture.ReadPixels(new Rect(0, 0, size, size), 0, 0);
		texture.Apply();
		RenderTexture.active = previous;

		Color[] pixels = texture.GetPixels();
		Object.DestroyImmediate(texture);
		return pixels;
	}

	static void WriteCubemap(Color[][] pixels)
	{
		// Orientation is decided against ground truth rather than against the faces' agreement
		// with each other: we chose where to put the sun, so we know which texel it must land
		// on. An earlier version compared the +Z/+Y seam in the read-back arrays, which cannot
		// work - a vertical flip applied consistently to every face leaves that comparison
		// unchanged while still producing seams once the faces are assembled.
		OrientationCheck check = CheckOrientation(pixels[0], pixels[1]);
		bool flip = check.flip;

		if (flip)
		{
			for (int f = 0; f < 6; f++) { pixels[f] = FlipRows(pixels[f], CubemapSize); }
		}

		var cubemap = new Cubemap(CubemapSize, TextureFormat.RGBAHalf, mipChain: false);
		for (int f = 0; f < 6; f++) { cubemap.SetPixels(pixels[f], (CubemapFace)f); }
		cubemap.Apply();

		AssetDatabase.DeleteAsset(CubemapPath);
		AssetDatabase.CreateAsset(cubemap, CubemapPath);
		AssetDatabase.SaveAssets();

		// Shown as a dialog rather than only logged: the orientation verdict is the one piece
		// of evidence that says whether the bake is trustworthy, and it is no use sitting in a
		// Console nobody is filtering to Info.
		string verdict =
			$"Baked {CubemapSize}x{CubemapSize}, sun frozen at {CubemapSunElevationDegrees} deg.\n\n" +
			"Orientation check (sun located by differencing +X against -X)\n" +
			$"  peak at row {check.row:F1}, column offset {check.columnOffset:+0.000;-0.000}\n" +
			$"  row {check.expectedDirect:F1} unflipped vs {check.expectedFlipped:F1} flipped " +
			$"-> using {(flip ? "FLIPPED" : "direct")} row order\n" +
			$"  implied sun elevation {check.impliedElevationDegrees:F1} deg " +
			$"(actual {CubemapSunElevationDegrees:F1})\n\n" +
			(check.confident
				? "Orientation looks correct."
				: "The sun did not land where expected. This check has produced false alarms " +
				  "before, so treat it as advisory: LOOK AT THE SKY. A seam at the horizon is " +
				  "the symptom that matters, and the row-order decision above is robust even " +
				  "when the elevation estimate is not.");

		Debug.Log($"[BaselineSky] {CubemapPath}\n{verdict}");
		EditorUtility.DisplayDialog("Sky cubemap baked", verdict, "OK");

		Selection.activeObject = cubemap;
	}

	/// <summary>
	/// Decides the row order by finding the sun.
	///
	/// The sun is placed at a known elevation in the plane of +X and up, so on the +X face it
	/// must land at a computable texel: that face is parameterised as dir proportional to
	/// (1, -v, -u), and normalising the sun direction by its x component gives (1, tan e, 0),
	/// hence u = 0 and v = -tan(e). The two row-order hypotheses put it about 119 rows apart
	/// at 256^2, so the measurement is not close to ambiguous.
	///
	/// The bake contains no sun disc - that is added in the shader - but the Mie forward lobe
	/// peaks at the same direction, so the brightest texel is still the sun.
	/// </summary>
	struct OrientationCheck
	{
		public bool flip;
		public float row, expectedDirect, expectedFlipped;
		public float impliedElevationDegrees;
		public float columnOffset;
		public bool confident;
	}

	/// <summary>
	/// Locates the sun by differencing the +X face against -X.
	///
	/// Brightness alone does not find the sun. For the same (u,v) the two faces are exact
	/// mirrors about the up axis - +X is (1,-v,-u) and -X is (-1,-v,u), so the elevation
	/// component and the vector length are identical - which means the air-mass structure that
	/// dominates absolute brightness cancels exactly, leaving the sun's forward scattering.
	///
	/// Two earlier attempts failed on this. Taking the brightest texel put the answer 5 deg
	/// low, because scattered radiance peaks below the sun where the path is longer; then after
	/// downward rays were clamped to the horizon it put it at 3 deg, having simply found the
	/// horizon. Both times the bake was correct and the check was not.
	/// </summary>
	static int FindSunTexel(Color[] positiveX, Color[] negativeX)
	{
		int best = 0;
		float bestDifference = float.MinValue;

		for (int i = 0; i < positiveX.Length; i++)
		{
			Color toward = positiveX[i];
			Color away = negativeX[i];
			float difference = (toward.r + toward.g + toward.b) - (away.r + away.g + away.b);

			if (difference > bestDifference) { bestDifference = difference; best = i; }
		}
		return best;
	}

	static OrientationCheck CheckOrientation(Color[] positiveX, Color[] negativeX)
	{
		var check = new OrientationCheck();

		float v = -Mathf.Tan(CubemapSunElevationDegrees * Mathf.Deg2Rad);
		float idY = (v + 1f) * 0.5f * CubemapSize - 0.5f;

		// Read-back arrays are row 0 = bottom, while the compute writes id.y = 0 at the top.
		check.expectedDirect = CubemapSize - 1 - idY;
		check.expectedFlipped = idY;

		int sunTexel = FindSunTexel(positiveX, negativeX);

		check.row = sunTexel / CubemapSize;
		float column = sunTexel % CubemapSize;

		// The decision is binary and the alternatives are ~119 rows apart, so nearness decides
		// it robustly even though neither prediction is hit exactly.
		check.flip = Mathf.Abs(check.row - check.expectedFlipped)
			< Mathf.Abs(check.row - check.expectedDirect);

		float measuredIdY = check.flip ? check.row : CubemapSize - 1 - check.row;
		float measuredV = (measuredIdY + 0.5f) / CubemapSize * 2f - 1f;
		check.impliedElevationDegrees = Mathf.Atan(-measuredV) * Mathf.Rad2Deg;
		check.columnOffset = (column + 0.5f) / CubemapSize * 2f - 1f;

		// Differencing removes the air-mass gradient, so the residual error should be small -
		// but the Mie lobe is broad, so this is a sanity check rather than a precise fix.
		bool elevationPlausible = Mathf.Abs(check.impliedElevationDegrees - CubemapSunElevationDegrees) < 8f;
		bool centred = Mathf.Abs(check.columnOffset) < 0.05f;
		check.confident = elevationPlausible && centred;

		return check;
	}

	static Color[] FlipRows(Color[] pixels, int size)
	{
		var flipped = new Color[pixels.Length];
		for (int y = 0; y < size; y++)
		{
			System.Array.Copy(pixels, y * size, flipped, (size - 1 - y) * size, size);
		}
		return flipped;
	}

	static AtmosphereEffect FindAtmosphere()
	{
		foreach (string guid in AssetDatabase.FindAssets("t:AtmosphereEffect"))
		{
			var effect = AssetDatabase.LoadAssetAtPath<AtmosphereEffect>(
				AssetDatabase.GUIDToAssetPath(guid));
			if (effect != null) { return effect; }
		}
		return null;
	}

	static void ConfigureImporter(string path)
	{
		var importer = AssetImporter.GetAtPath(path) as TextureImporter;
		if (importer == null) { return; }

		importer.sRGBTexture = false;
		importer.mipmapEnabled = false;
		importer.wrapMode = TextureWrapMode.Clamp;
		importer.filterMode = FilterMode.Bilinear;
		importer.textureCompression = TextureImporterCompression.Uncompressed;
		importer.SaveAndReimport();
	}
}
