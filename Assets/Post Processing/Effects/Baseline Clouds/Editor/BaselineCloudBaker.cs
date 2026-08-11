// No `using System.IO` - Assets/Scripts/Types/Shape.cs declares a `Path` struct in the global
// namespace, and a global type beats a using-imported one, so System.IO.Path resolves to that.
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Clouds
{
	/// <summary>
	/// Bakes the baseline cloud layers, and gives the settings asset a viewer so they can be checked
	/// without rendering anything.
	///
	/// Editor-invoked and written to Assets/Data, following the rest of the generation tooling. The
	/// baseline is meant to be cheap at run time, so producing its textures offline is not merely
	/// convenient - a baseline that generated its own content every frame would not be the technique
	/// the comparison is about.
	/// </summary>
	[CustomEditor(typeof(BaselineCloudSettings))]
	public class BaselineCloudBaker : UnityEditor.Editor
	{
		const string ComputePath =
			"Assets/Post Processing/Effects/Baseline Clouds/Compute/BaselineCloudBake.compute";

		[SerializeField] int previewChannel = 3;   // density by default
		[SerializeField] bool previewUpper;
		Texture2D preview;

		public override void OnInspectorGUI()
		{
			DrawDefaultInspector();

			var settings = (BaselineCloudSettings)target;

			EditorGUILayout.Space();
			EditorGUILayout.LabelField("Authored bake", EditorStyles.boldLabel);
			EditorGUILayout.HelpBox(
				"A procedural cloud field - the honest baseline, and what a studio would author.\n\n" +
				"The volumetric capture, which writes the same format so the two are drop-in " +
				"replacements, is a separate bake.",
				MessageType.None);

			using (new EditorGUILayout.HorizontalScope())
			{
				if (GUILayout.Button("Bake Both Layers", GUILayout.Height(28))) { BakeBoth(settings); }
				if (GUILayout.Button("Lower", GUILayout.Height(28))) { Bake(settings, upper: false); }
				if (GUILayout.Button("Upper", GUILayout.Height(28))) { Bake(settings, upper: true); }
			}

			EditorGUILayout.Space();
			EditorGUILayout.LabelField("Capture from the volumetric renderer", EditorStyles.boldLabel);
			EditorGUILayout.HelpBox(
				"Flattens the volumetric clouds into the same format. Not a shippable baseline - it " +
				"needs the volumetric renderer to exist - but a control condition: the content is " +
				"then identical, so any remaining visual difference is the technique rather than the " +
				"art.\n\nStamped against the volumetric's parameters, so it reports BAKE_STALE if " +
				"those move.",
				MessageType.None);

			if (GUILayout.Button("Capture Both Layers From Volumetric", GUILayout.Height(28)))
			{
				CaptureBoth(settings);
			}

			DrawPreview(settings);
		}

		// ------------------------------------------------------------------ preview

		void DrawPreview(BaselineCloudSettings settings)
		{
			EditorGUILayout.Space();
			EditorGUILayout.LabelField("Viewer", EditorStyles.boldLabel);

			EditorGUI.BeginChangeCheck();
			previewUpper = EditorGUILayout.Toggle("Upper layer", previewUpper);
			previewChannel = EditorGUILayout.IntPopup(
				"Channel", previewChannel,
				new[] { "Density (A)", "Height (B)", "Normal X (R)", "Normal Y (G)", "Normal (RG)" },
				new[] { 3, 2, 0, 1, -1 });
			if (EditorGUI.EndChangeCheck()) { preview = null; }

			var baked = AssetDatabase.LoadAssetAtPath<Texture2D>(settings.AuthoredPath(previewUpper));
			if (baked == null)
			{
				EditorGUILayout.HelpBox("That layer has not been baked yet.", MessageType.Info);
				return;
			}

			if (preview == null) { preview = BuildPreview(baked, previewChannel); }
			if (preview == null)
			{
				EditorGUILayout.HelpBox("Texture is not readable.", MessageType.Warning);
				return;
			}

			Rect rect = GUILayoutUtility.GetAspectRect(2f);
			GUI.DrawTexture(rect, preview, ScaleMode.ScaleToFit);
			EditorGUILayout.HelpBox(
				"Density should read as broken cloud with genuinely clear gaps. A field that is all " +
				"mid-grey means the range correction is not doing its job.",
				MessageType.None);
		}

		static Texture2D BuildPreview(Texture2D source, int channel)
		{
			if (!source.isReadable) { return null; }

			Color[] pixels = source.GetPixels();
			var output = new Color[pixels.Length];

			for (int i = 0; i < pixels.Length; i++)
			{
				Color c = pixels[i];
				output[i] = channel < 0
					? new Color(c.r, c.g, 0.5f, 1f)
					: new Color(c[channel], c[channel], c[channel], 1f);
			}

			var tex = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false)
			{
				hideFlags = HideFlags.HideAndDontSave
			};
			tex.SetPixels(output);
			tex.Apply();
			return tex;
		}

		// ------------------------------------------------------------------ bake

		// ------------------------------------------------------------------ capture

		static void CaptureBoth(BaselineCloudSettings settings)
		{
			var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
			if (compute == null) { return; }

			CloudEffect volumetric = FindVolumetric();
			if (volumetric == null)
			{
				Debug.LogError("[Baseline clouds] no CloudEffect asset found - there is nothing to " +
					"capture. The captured layers are derived from the volumetric renderer.");
				return;
			}

			Capture(compute, settings, volumetric, upper: false);
			Capture(compute, settings, volumetric, upper: true);
		}

		static void Capture(ComputeShader compute, BaselineCloudSettings settings,
			CloudEffect volumetric, bool upper)
		{
			int width = Mathf.Max(64, settings.resolution);
			int height = Mathf.Max(32, width / 2);

			RenderTexture captured = CreateTarget(width, height);
			RenderTexture shaded = CreateTarget(width, height);

			try
			{
				int captureKernel = compute.FindKernel("CaptureVolumetric");

				if (!volumetric.ApplyDensityValuesTo(compute, captureKernel))
				{
					Debug.LogError("[Baseline clouds] the CloudEffect has no baked volumes or weather " +
						"compute assigned, so its density model cannot be evaluated.");
					return;
				}

				compute.SetTexture(captureKernel, "Result", captured);
				compute.SetVector("outputSize", new Vector2(width, height));
				compute.SetInt("captureSteps", settings.captureSteps);
				compute.SetFloat("captureAbsorption", settings.captureAbsorption);
				compute.SetFloat("captureThreshold", settings.captureThreshold);

				// Each layer captures half the shell, so the two parallax against one another rather
				// than being the same sheet drawn twice. Set AFTER ApplyDensityValuesTo, which binds
				// the volumetric's own full-shell radii - these deliberately override them for the
				// span of the march, and only for that.
				float inner = volumetric.InnerRadius;
				float outer = volumetric.OuterRadius;
				float middle = Mathf.Lerp(inner, outer, 0.5f);
				compute.SetFloat("captureFloor", upper ? middle : inner);
				compute.SetFloat("captureCeiling", upper ? outer : middle);

				int groupsX = Mathf.CeilToInt(width / 8f);
				int groupsY = Mathf.CeilToInt(height / 8f);
				compute.Dispatch(captureKernel, groupsX, groupsY, 1);

				int normalKernel = compute.FindKernel("ComputeNormals");
				compute.SetTexture(normalKernel, "Source", captured);
				compute.SetTexture(normalKernel, "Result", shaded);
				compute.SetFloat("normalStrength", settings.captureNormalStrength);
				compute.Dispatch(normalKernel, groupsX, groupsY, 1);

				Save(shaded, width, height, settings.BakedPath(upper));

				SkyBakeStampWriter.Record(
					settings.BakedPath(upper), SkyBakeStamp.Recipe.Clouds,
					null, null,
					new SkyBakeStamp.Inputs()
						.Add("resolution", width)
						.Add("captureSteps", settings.captureSteps)
						.Add("captureAbsorption", settings.captureAbsorption)
						.Add("upperLayer", upper ? 1 : 0),
					volumetric);
			}
			finally
			{
				captured.Release();
				Object.DestroyImmediate(captured);
				shaded.Release();
				Object.DestroyImmediate(shaded);
			}
		}

		static CloudEffect FindVolumetric()
		{
			foreach (string guid in AssetDatabase.FindAssets("t:CloudEffect"))
			{
				var effect = AssetDatabase.LoadAssetAtPath<CloudEffect>(
					AssetDatabase.GUIDToAssetPath(guid));
				if (effect != null) { return effect; }
			}
			return null;
		}

		static RenderTexture CreateTarget(int width, int height)
		{
			var rt = new RenderTexture(width, height, 0, GraphicsFormat.R8G8B8A8_UNorm)
			{
				enableRandomWrite = true,
				wrapModeU = TextureWrapMode.Repeat,
				wrapModeV = TextureWrapMode.Clamp,
				filterMode = FilterMode.Bilinear
			};
			rt.Create();
			return rt;
		}

		// ------------------------------------------------------------------ authored

		static void BakeBoth(BaselineCloudSettings settings)
		{
			Bake(settings, upper: false);
			Bake(settings, upper: true);
		}

		static void Bake(BaselineCloudSettings settings, bool upper)
		{
			var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
			if (compute == null)
			{
				Debug.LogError($"Could not load the baseline cloud bake compute at {ComputePath}");
				return;
			}

			int width = Mathf.Max(64, settings.resolution);
			int height = Mathf.Max(32, width / 2);

			var target = new RenderTexture(width, height, 0, GraphicsFormat.R8G8B8A8_UNorm)
			{
				enableRandomWrite = true,
				wrapModeU = TextureWrapMode.Repeat,   // longitude wraps
				wrapModeV = TextureWrapMode.Clamp,    // latitude does not
				filterMode = FilterMode.Bilinear
			};
			target.Create();

			try
			{
				BaselineCloudSettings.Layer layer = upper ? settings.upper : settings.lower;

				int kernel = compute.FindKernel("BakeAuthored");
				compute.SetTexture(kernel, "Result", target);
				compute.SetVector("outputSize", new Vector2(width, height));
				compute.SetInt("octaves", Mathf.Max(1, layer.octaves));
				compute.SetFloat("lacunarity", layer.lacunarity);
				compute.SetFloat("gain", layer.gain);
				compute.SetFloat("rangeGain", settings.rangeGain);
				compute.SetFloat("fieldScale", layer.scale);
				compute.SetFloat("coverageScale", layer.coverageScale);
				compute.SetFloat("fieldBias", layer.bias);
				compute.SetFloat("fieldContrast", layer.contrast);
				compute.SetFloat("seedOffset", layer.seedOffset);
				compute.SetFloat("reliefStrength", layer.reliefStrength);
				compute.SetVector("driftOffset", Vector4.zero);

				compute.Dispatch(kernel,
					Mathf.CeilToInt(width / 8f), Mathf.CeilToInt(height / 8f), 1);

				Save(target, width, height, settings.AuthoredPath(upper));
			}
			finally
			{
				target.Release();
				Object.DestroyImmediate(target);
			}
		}

		/// <summary>
		/// Reads the target back and writes it as a PNG.
		///
		/// A 2D texture comes back in one request, unlike the volumetric's 3D noise which has to be
		/// read a slice at a time.
		/// </summary>
		internal static void Save(RenderTexture target, int width, int height, string path)
		{
			AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32);
			request.WaitForCompletion();

			if (request.hasError)
			{
				Debug.LogError($"Baseline cloud readback failed for {path}");
				return;
			}

			// linear:true so ReadPixels applies no transfer curve. RG hold a direction and B a
			// height; neither would survive one.
			var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, linear: true);
			texture.SetPixelData(request.GetData<byte>(), 0);
			texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);

			byte[] png = texture.EncodeToPNG();
			Object.DestroyImmediate(texture);

			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
			System.IO.File.WriteAllBytes(path, png);
			AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
			ApplyImportSettings(path);

			Debug.Log($"Baked {path} ({width}x{height})");
			EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Texture2D>(path));
		}

		static void ApplyImportSettings(string path)
		{
			var importer = AssetImporter.GetAtPath(path) as TextureImporter;
			if (importer == null) { return; }

			// Not sRGB and uncompressed, because RG carry a direction: a transfer curve would bend it
			// and a block compressor would band it, and the relief shading reads both directly.
			importer.textureType = TextureImporterType.Default;
			importer.sRGBTexture = false;
			importer.textureCompression = TextureImporterCompression.Uncompressed;

			// Mips, unlike the shadow map: this layer is sampled where the view ray meets the shell,
			// which is heavy minification near the horizon.
			importer.mipmapEnabled = true;
			importer.filterMode = FilterMode.Bilinear;
			importer.wrapModeU = TextureWrapMode.Repeat;
			importer.wrapModeV = TextureWrapMode.Clamp;
			importer.isReadable = true;   // the inspector viewer reads it back
			importer.SaveAndReimport();
		}
	}
}
