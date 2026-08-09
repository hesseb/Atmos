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
	const int AzimuthSamples = 32;

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
		compute.SetInt("numAzimuthSamples", AzimuthSamples);
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
			$"  {ScatteringSteps} scattering steps, {AzimuthSamples} azimuth samples, " +
			$"observer {BakeAltitude} above the surface\n" +
			$"  radiance range {min:F4} .. {max:F4}\n" +
			"  Azimuth is averaged out, so the Mie forward lobe is missing by construction - " +
			"the shader's glow term is what stands in for it.");

		Selection.activeObject = AssetDatabase.LoadAssetAtPath<Texture2D>(BakedPath);
	}

	// ------------------------------------------------------------------ cubemap

	const string CubemapPath = Folder + "/SkyCubemap.asset";
	const int CubemapSize = 256;

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
		// Cube map face orientation silently mirrors or rotates if the convention is wrong,
		// and the result still looks like a sky - just with seams. Rather than trust the
		// convention, measure the discontinuity across a seam both ways and take the better.
		// The numbers are logged, so a wrong answer is visible instead of assumed.
		float direct = SeamError(pixels, flipVertically: false);
		float flipped = SeamError(pixels, flipVertically: true);
		bool flip = flipped < direct;

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

		Debug.Log($"[BaselineSky] baked {CubemapSize}^2 sky cubemap to {CubemapPath}\n" +
			$"  frozen at sun elevation {CubemapSunElevationDegrees} deg, observer " +
			$"{BakeAltitude} above the surface, {ScatteringSteps} scattering steps\n" +
			$"  seam error: direct {direct:F5}, flipped {flipped:F5} -> using " +
			$"{(flip ? "FLIPPED" : "direct")} row order\n" +
			"  If both numbers are large the face basis is wrong, not just the row order - " +
			"expect visible seams.");

		Selection.activeObject = cubemap;
	}

	/// <summary>
	/// Mean absolute difference across the seam where the +Z face meets the +Y face. Those two
	/// view almost the same directions along their shared edge, so a correct assembly makes
	/// this near zero and a vertical flip makes it large.
	/// </summary>
	static float SeamError(Color[][] pixels, bool flipVertically)
	{
		Color[] side = flipVertically ? FlipRows(pixels[4], CubemapSize) : pixels[4];   // +Z
		Color[] top = flipVertically ? FlipRows(pixels[2], CubemapSize) : pixels[2];    // +Y

		// GetPixels is row 0 = bottom, so +Z's shared edge with +Y is its top row.
		int sideRow = CubemapSize - 1;
		const int topRow = 0;

		float total = 0f;
		for (int x = 0; x < CubemapSize; x++)
		{
			Color a = side[sideRow * CubemapSize + x];
			Color b = top[topRow * CubemapSize + x];
			total += Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);
		}
		return total / (CubemapSize * 3f);
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
