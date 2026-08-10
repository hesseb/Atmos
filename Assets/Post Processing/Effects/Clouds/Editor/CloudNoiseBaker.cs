// No `using System.IO` - Assets/Scripts/Types/Shape.cs declares a `Path` struct in the global
// namespace, and a global type beats a using-imported one, so System.IO.Path would resolve to that.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Clouds
{
	/// <summary>
	/// Bakes the cloud shape and detail volumes from a <see cref="CloudNoiseSettings"/> asset, and
	/// gives that asset a slice viewer so the result can be checked without rendering anything.
	///
	/// Offline, like the rest of the generation tooling - the volumes are Texture3D assets under
	/// Assets/Data, not something regenerated at load. The reference project instead regenerates
	/// into a RenderTexture keyed by scene name and calls FindObjectOfType every frame from
	/// OnRenderImage; neither is wanted here, least of all in the path the benchmark measures.
	/// </summary>
	[CustomEditor(typeof(CloudNoiseSettings))]
	public class CloudNoiseBaker : UnityEditor.Editor
	{
		const string ComputePath = "Assets/Post Processing/Effects/Clouds/Compute/CloudNoiseGen.compute";

		[SerializeField] float previewSlice;
		[SerializeField] int previewChannel = -1;   // -1 = all channels
		Texture2D preview;
		bool previewIsDetail;

		// GetPixels on a 128^3 volume allocates 32 MB. The slider calls this every frame while it is
		// being dragged, so the whole volume is pulled back once and kept until the source changes.
		Color[] cachedPixels;
		Texture3D cachedSource;

		public override void OnInspectorGUI()
		{
			DrawDefaultInspector();

			var settings = (CloudNoiseSettings)target;

			EditorGUILayout.Space();
			EditorGUILayout.LabelField("Bake", EditorStyles.boldLabel);
			EditorGUILayout.LabelField(
				"Cost",
				$"shape {Cubed(settings.shapeResolution)}, detail {Cubed(settings.detailResolution)} texels");

			using (new EditorGUILayout.HorizontalScope())
			{
				if (GUILayout.Button("Bake Both", GUILayout.Height(28))) { Bake(settings, true, true); }
				if (GUILayout.Button("Shape Only", GUILayout.Height(28))) { Bake(settings, true, false); }
				if (GUILayout.Button("Detail Only", GUILayout.Height(28))) { Bake(settings, false, true); }
			}

			DrawPreview(settings);
		}

		static string Cubed(int n) => $"{(long)n * n * n / 1000L}k";

		// ------------------------------------------------------------------ preview

		void DrawPreview(CloudNoiseSettings settings)
		{
			EditorGUILayout.Space();
			EditorGUILayout.LabelField("Slice Viewer", EditorStyles.boldLabel);

			var shape = AssetDatabase.LoadAssetAtPath<Texture3D>(settings.ShapePath);
			var detail = AssetDatabase.LoadAssetAtPath<Texture3D>(settings.DetailPath);

			if (shape == null && detail == null)
			{
				EditorGUILayout.HelpBox("Nothing baked yet.", MessageType.Info);
				return;
			}

			using (new EditorGUILayout.HorizontalScope())
			{
				using (new EditorGUI.DisabledScope(shape == null))
				{
					if (GUILayout.Button("Shape")) { previewIsDetail = false; preview = null; }
				}
				using (new EditorGUI.DisabledScope(detail == null))
				{
					if (GUILayout.Button("Detail")) { previewIsDetail = true; preview = null; }
				}
			}

			Texture3D source = previewIsDetail ? detail : shape;
			if (source == null) { return; }

			EditorGUI.BeginChangeCheck();
			previewSlice = EditorGUILayout.Slider("Depth", previewSlice, 0, 1);
			previewChannel = EditorGUILayout.IntPopup(
				"Channel", previewChannel,
				new[] { "All (RGB)", "R", "G", "B", "A" },
				new[] { -1, 0, 1, 2, 3 });
			if (EditorGUI.EndChangeCheck()) { preview = null; }

			if (preview == null) { preview = BuildSlice(source, previewSlice, previewChannel, ref cachedSource, ref cachedPixels); }

			if (preview != null)
			{
				Rect rect = GUILayoutUtility.GetAspectRect(1f);
				// Tiled 2x2 so the wrap is visible. The volumes must tile seamlessly - a break here
				// draws a straight line of cloud the height of the sky once it is marched.
				GUI.DrawTextureWithTexCoords(rect, preview, new Rect(0, 0, 2, 2));
				EditorGUILayout.HelpBox(
					"Drawn tiled 2x2. Any visible seam across the middle means the noise does not wrap.",
					MessageType.None);
			}
		}

		static Texture2D BuildSlice(
			Texture3D source, float depth01, int channel, ref Texture3D cachedSource, ref Color[] cachedPixels)
		{
			if (!source.isReadable) { return null; }

			int size = source.width;
			int z = Mathf.Clamp(Mathf.RoundToInt(depth01 * (size - 1)), 0, size - 1);

			if (cachedSource != source || cachedPixels == null)
			{
				cachedPixels = source.GetPixels(0);
				cachedSource = source;
			}

			Color[] all = cachedPixels;
			var slice = new Color[size * size];
			int offset = z * size * size;

			for (int i = 0; i < slice.Length; i++)
			{
				Color c = all[offset + i];
				slice[i] = channel < 0
					? new Color(c.r, c.g, c.b, 1f)
					: new Color(c[channel], c[channel], c[channel], 1f);
			}

			var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
			tex.SetPixels(slice);
			tex.wrapMode = TextureWrapMode.Repeat;
			tex.filterMode = FilterMode.Point;
			tex.Apply();
			return tex;
		}

		// ------------------------------------------------------------------ bake

		static void Bake(CloudNoiseSettings settings, bool doShape, bool doDetail)
		{
			var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
			if (compute == null)
			{
				Debug.LogError($"Could not load the cloud noise compute at {ComputePath}");
				return;
			}

			try
			{
				if (doShape)
				{
					BakeVolume(compute, settings, settings.shape, settings.shapeResolution,
						settings.ShapePath, usePerlinOnFirstChannel: true);
				}
				if (doDetail)
				{
					BakeVolume(compute, settings, settings.detail, settings.detailResolution,
						settings.DetailPath, usePerlinOnFirstChannel: false);
				}
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}
		}

		static void BakeVolume(
			ComputeShader compute,
			CloudNoiseSettings settings,
			CloudNoiseSettings.WorleyChannel[] channels,
			int resolution,
			string path,
			bool usePerlinOnFirstChannel)
		{
			var volume = new RenderTexture(resolution, resolution, 0, GraphicsFormat.R16G16B16A16_UNorm)
			{
				volumeDepth = resolution,
				dimension = TextureDimension.Tex3D,
				enableRandomWrite = true,
				wrapMode = TextureWrapMode.Repeat,
				filterMode = FilterMode.Bilinear
			};
			volume.Create();

			int noiseKernel = compute.FindKernel("CSNoise");
			int normalizeKernel = compute.FindKernel("CSNormalize");
			int groups = Mathf.CeilToInt(resolution / 8f);

			var buffers = new List<ComputeBuffer>();

			try
			{
				for (int channel = 0; channel < channels.Length && channel < 4; channel++)
				{
					EditorUtility.DisplayProgressBar(
						$"Baking {System.IO.Path.GetFileNameWithoutExtension(path)}",
						$"Channel {"RGBA"[channel]}", channel / (float)channels.Length);

					CloudNoiseSettings.WorleyChannel c = channels[channel];

					compute.SetInt("resolution", resolution);
					compute.SetVector("channelMask", ChannelMask(channel));
					compute.SetFloat("persistence", c.persistence);
					compute.SetInt("numCellsA", c.divisionsA);
					compute.SetInt("numCellsB", c.divisionsB);
					compute.SetInt("numCellsC", c.divisionsC);
					compute.SetBool("invertNoise", c.invert);
					compute.SetInt("tile", Mathf.Max(1, c.tile));

					// Only the shape volume's R channel is Perlin-Worley; everything else is pure
					// Worley FBM, exactly as Schneider packs them.
					bool perlin = usePerlinOnFirstChannel && channel == 0;
					compute.SetBool("usePerlinWorley", perlin);
					compute.SetInt("perlinPeriod", Mathf.Max(1, settings.perlinPeriod));
					compute.SetInt("perlinOctaves", Mathf.Max(1, settings.perlinOctaves));
					compute.SetFloat("perlinPersistence", settings.perlinPersistence);
					compute.SetFloat("perlinGain", settings.perlinGain);

					var prng = new System.Random(c.seed);
					buffers.Add(SetPoints(compute, noiseKernel, prng, c.divisionsA, "pointsA"));
					buffers.Add(SetPoints(compute, noiseKernel, prng, c.divisionsB, "pointsB"));
					buffers.Add(SetPoints(compute, noiseKernel, prng, c.divisionsC, "pointsC"));

					var minMax = new ComputeBuffer(2, sizeof(int), ComputeBufferType.Structured);
					minMax.SetData(new[] { int.MaxValue, 0 });
					buffers.Add(minMax);

					compute.SetTexture(noiseKernel, "Result", volume);
					compute.SetBuffer(noiseKernel, "minMax", minMax);
					compute.Dispatch(noiseKernel, groups, groups, groups);

					// Rescales this channel to fill [0,1]. Worley never reaches either end on its
					// own, and the density model's remaps assume a full range.
					compute.SetTexture(normalizeKernel, "Result", volume);
					compute.SetBuffer(normalizeKernel, "minMax", minMax);
					compute.SetVector("channelMask", ChannelMask(channel));
					compute.Dispatch(normalizeKernel, groups, groups, groups);
				}

				Save(volume, resolution, path);
			}
			finally
			{
				foreach (ComputeBuffer b in buffers) { b?.Release(); }
				volume.Release();
				Object.DestroyImmediate(volume);
			}
		}

		static Vector4 ChannelMask(int channel)
		{
			return new Vector4(channel == 0 ? 1 : 0, channel == 1 ? 1 : 0, channel == 2 ? 1 : 0, channel == 3 ? 1 : 0);
		}

		/// <summary>
		/// One jittered point per cell, laid out so the compute can index by
		/// x + n*(y + z*n). The prng is threaded through all three octaves so a seed reproduces the
		/// whole channel, matching the reference.
		/// </summary>
		static ComputeBuffer SetPoints(ComputeShader compute, int kernel, System.Random prng, int cellsPerAxis, string name)
		{
			var points = new Vector3[cellsPerAxis * cellsPerAxis * cellsPerAxis];
			float cellSize = 1f / cellsPerAxis;

			for (int x = 0; x < cellsPerAxis; x++)
			{
				for (int y = 0; y < cellsPerAxis; y++)
				{
					for (int z = 0; z < cellsPerAxis; z++)
					{
						var jitter = new Vector3(
							(float)prng.NextDouble(), (float)prng.NextDouble(), (float)prng.NextDouble()) * cellSize;
						int index = x + cellsPerAxis * (y + z * cellsPerAxis);
						points[index] = new Vector3(x, y, z) * cellSize + jitter;
					}
				}
			}

			var buffer = new ComputeBuffer(points.Length, sizeof(float) * 3, ComputeBufferType.Structured);
			buffer.SetData(points);
			compute.SetBuffer(kernel, name, buffer);
			return buffer;
		}

		/// <summary>
		/// Reads the volume back and writes it as a Texture3D asset.
		///
		/// A single readback rather than the reference's slice-by-slice compute plus ReadPixels -
		/// same result, far less machinery. RGBA32 matches Schneider's own 8-bit volumes and keeps
		/// the shape volume at 8 MB rather than 16.
		/// </summary>
		static void Save(RenderTexture volume, int resolution, string path)
		{
			AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(volume, 0, TextureFormat.RGBA32);
			request.WaitForCompletion();

			if (request.hasError)
			{
				Debug.LogError($"Cloud noise readback failed for {path}");
				return;
			}

			var texture = new Texture3D(resolution, resolution, resolution, TextureFormat.RGBA32, false)
			{
				wrapMode = TextureWrapMode.Repeat,
				filterMode = FilterMode.Bilinear
			};
			texture.SetPixelData(request.GetData<byte>(), 0);
			texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);

			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

			var existing = AssetDatabase.LoadAssetAtPath<Texture3D>(path);
			if (existing != null)
			{
				// Overwrite in place so the GUID survives and anything referencing the volume keeps
				// pointing at it - the same reason RendererProfiles.Save uses CopySerialized.
				EditorUtility.CopySerialized(texture, existing);
				Object.DestroyImmediate(texture);
				AssetDatabase.SaveAssets();
			}
			else
			{
				AssetDatabase.CreateAsset(texture, path);
			}

			AssetDatabase.Refresh();
			Debug.Log($"Baked {path} ({resolution}^3)");
		}
	}
}
