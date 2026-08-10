// No `using System.IO` - Assets/Scripts/Types/Shape.cs declares a `Path` struct in the global
// namespace, and a global type beats a using-imported one, so System.IO.Path would resolve to that
// instead. Fully qualified below rather than aliased, so the next reader does not have to wonder.
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Generation
{
	/// <summary>
	/// Bakes the city light field the ocean uses to know where the cities are.
	///
	/// An offline tool, like the rest of Assets/Scripts/Generation - the output is a texture checked
	/// into Assets/Data, not something regenerated at load. Run it from Testbed > Generation > Bake
	/// City Light Field whenever the source night-lights map changes, which in practice is never.
	///
	/// See CityLightField.compute for what the four channels hold and why the gather is done in
	/// angular space.
	/// </summary>
	public class CityLightFieldBaker : EditorWindow
	{
		const string ComputePath = "Assets/Scripts/Generation/City Light Field/CityLightField.compute";
		const string DefaultSourcePath = "Assets/Data/City Lights/Light map small.jpg";
		const string DefaultOutputPath = "Assets/Data/City Lights/City Light Field.png";

		// Rows per dispatch. See the rowStart note in the compute: one dispatch for the whole image
		// is long enough to trip the Windows GPU watchdog and reset the driver mid-bake.
		const int RowsPerBand = 32;

		Texture2D source;
		string outputPath = DefaultOutputPath;

		// 2048x1024 is deliberately *lower* than the 4096x2048 source. This is a glow field, band
		// limited by construction, so the resolution it needs is set by how fast the glow varies and
		// not by the detail in the source - and staying small is what keeps it smooth and cheap.
		int width = 2048;
		int height = 1024;

		int numRadialSteps = 24;
		int numAngularSteps = 32;

		// Angles are in radians of arc across the globe, so they mean the same thing at every world
		// scale. 0.0015 is roughly a city; 0.05 is roughly the distance its glow carries.
		float minAngle = 0.0015f;
		float maxAngle = 0.05f;
		float falloffPower = 2f;
		float litThreshold = 0.15f;

		[MenuItem("Testbed/Generation/Bake City Light Field")]
		static void Open()
		{
			GetWindow<CityLightFieldBaker>("City Light Field").Show();
		}

		void OnEnable()
		{
			if (source == null)
			{
				source = AssetDatabase.LoadAssetAtPath<Texture2D>(DefaultSourcePath);
			}
		}

		void OnGUI()
		{
			EditorGUILayout.HelpBox(
				"Bakes the glow the ocean picks up from city lights.\n\n" +
				"RG: direction toward the light, in the local east/north basis.\n" +
				"B: the glow, stored as its square root for precision in the darks.\n" +
				"A: angular distance to the nearest lit land.",
				MessageType.None);

			source = (Texture2D)EditorGUILayout.ObjectField("Source Light Map", source, typeof(Texture2D), false);
			outputPath = EditorGUILayout.TextField("Output Path", outputPath);

			EditorGUILayout.Space();
			width = EditorGUILayout.IntField("Width", width);
			height = EditorGUILayout.IntField("Height", height);

			EditorGUILayout.Space();
			numRadialSteps = EditorGUILayout.IntSlider("Radial Steps", numRadialSteps, 4, 64);
			numAngularSteps = EditorGUILayout.IntSlider("Angular Steps", numAngularSteps, 8, 128);

			EditorGUILayout.Space();
			minAngle = EditorGUILayout.Slider("Min Angle (rad)", minAngle, 0.0002f, 0.02f);
			maxAngle = EditorGUILayout.Slider("Max Angle (rad)", maxAngle, 0.005f, 0.3f);
			falloffPower = EditorGUILayout.Slider("Falloff Power", falloffPower, 0.5f, 4f);
			litThreshold = EditorGUILayout.Slider("Lit Threshold", litThreshold, 0.01f, 0.9f);

			EditorGUILayout.Space();
			EditorGUILayout.LabelField(
				"Cost",
				$"{(long)width * height * numRadialSteps * numAngularSteps / 1_000_000L} M samples");

			EditorGUILayout.Space();
			using (new EditorGUI.DisabledScope(source == null || string.IsNullOrWhiteSpace(outputPath)))
			{
				if (GUILayout.Button("Bake", GUILayout.Height(30))) { Bake(); }
			}
		}

		void Bake()
		{
			ComputeShader compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
			if (compute == null)
			{
				Debug.LogError($"Could not load the bake compute shader at {ComputePath}");
				return;
			}

			var result = new RenderTexture(width, height, 0, GraphicsFormat.R32G32B32A32_SFloat)
			{
				enableRandomWrite = true,
				wrapMode = TextureWrapMode.Repeat,
				filterMode = FilterMode.Bilinear
			};
			result.Create();

			int kernel = compute.FindKernel("BakeCityLightField");
			compute.SetTexture(kernel, "LightMap", source);
			compute.SetTexture(kernel, "Result", result);
			compute.SetInts("outputSize", width, height);
			compute.SetFloats("lightMapSize", source.width, source.height);
			compute.SetInt("numRadialSteps", numRadialSteps);
			compute.SetInt("numAngularSteps", numAngularSteps);
			compute.SetFloat("minAngle", minAngle);
			compute.SetFloat("maxAngle", maxAngle);
			compute.SetFloat("falloffPower", falloffPower);
			compute.SetFloat("litThreshold", litThreshold);

			compute.GetKernelThreadGroupSizes(kernel, out uint groupX, out uint groupY, out _);

			try
			{
				for (int row = 0; row < height; row += RowsPerBand)
				{
					if (EditorUtility.DisplayCancelableProgressBar(
						"Baking city light field", $"Row {row} of {height}", row / (float)height))
					{
						return;
					}

					int rows = Mathf.Min(RowsPerBand, height - row);
					compute.SetInt("rowStart", row);
					compute.Dispatch(
						kernel,
						Mathf.CeilToInt(width / (float)groupX),
						Mathf.CeilToInt(rows / (float)groupY),
						1);

					// Forces the band to finish before the next is queued, which is the whole point
					// of banding - without it the driver batches them back into one long stall.
					GL.Flush();
				}

				Write(result);
			}
			finally
			{
				EditorUtility.ClearProgressBar();
				RenderTexture.active = null;
				result.Release();
				DestroyImmediate(result);
			}
		}

		void Write(RenderTexture result)
		{
			// linear:true so ReadPixels does not apply a transfer curve. The project is in Gamma
			// colour space so no conversion should happen anyway, but RG is a direction and B is
			// already sqrt-encoded - neither would survive one.
			var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, linear: true);

			RenderTexture previous = RenderTexture.active;
			RenderTexture.active = result;
			texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
			texture.Apply();
			RenderTexture.active = previous;

			byte[] png = texture.EncodeToPNG();
			DestroyImmediate(texture);

			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath));
			System.IO.File.WriteAllBytes(outputPath, png);
			AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceUpdate);
			ApplyImportSettings(outputPath);

			Debug.Log($"Baked city light field to {outputPath} ({width}x{height}, {png.Length / 1024} KB on disk)");
			EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Texture2D>(outputPath));
		}

		static void ApplyImportSettings(string path)
		{
			var importer = AssetImporter.GetAtPath(path) as TextureImporter;
			if (importer == null) { return; }

			// Uncompressed and not sRGB, both for the same reason: RG holds a direction. A block
			// compressor would smear it into visible banding in the glint, and a transfer curve
			// would bend it outright. B is sqrt-encoded by hand instead of relying on sRGB, so that
			// the direction channels can stay linear alongside it.
			importer.textureType = TextureImporterType.Default;
			importer.sRGBTexture = false;
			importer.textureCompression = TextureImporterCompression.Uncompressed;
			importer.mipmapEnabled = true;
			importer.filterMode = FilterMode.Bilinear;
			importer.wrapModeU = TextureWrapMode.Repeat;   // longitude wraps
			importer.wrapModeV = TextureWrapMode.Clamp;    // latitude does not
			importer.SaveAndReimport();
		}
	}
}
