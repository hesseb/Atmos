using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Clouds
{
	[CustomEditor( typeof( CloudNoiseSettings ) )]
	public class CloudNoiseBaker : UnityEditor.Editor
	{
		const string ComputePath = "Assets/Post Processing/Effects/PBR/Clouds/Compute/CloudNoiseGen.compute";
		const int AllChannels = -1;

		[SerializeField] float previewDepth;
		[SerializeField] int previewChannel = AllChannels;
		bool previewIsDetail;
		Texture2D preview;

		Color[] cachedPixels;
		Texture3D cachedSource;

		public override void OnInspectorGUI()
		{
			DrawDefaultInspector();

			var settings = (CloudNoiseSettings)target;

			EditorGUILayout.Space();
			EditorGUILayout.LabelField( "Bake", EditorStyles.boldLabel );
			EditorGUILayout.LabelField(
				"Cost",
				$"shape {Cubed( settings.shapeResolution )}, detail {Cubed( settings.detailResolution )} texels" );

			using ( new EditorGUILayout.HorizontalScope() )
			{
				if ( GUILayout.Button( "Bake Both", GUILayout.Height( 28 ) ) ) { Bake( settings, true, true ); }
				if ( GUILayout.Button( "Shape Only", GUILayout.Height( 28 ) ) ) { Bake( settings, true, false ); }
				if ( GUILayout.Button( "Detail Only", GUILayout.Height( 28 ) ) ) { Bake( settings, false, true ); }
			}

			DrawPreview( settings );
		}

		void OnDisable()
		{
			ClearPreview();
		}

		static string Cubed( int n ) => $"{(long)n * n * n / 1000L}k";

		void Bake( CloudNoiseSettings settings, bool bakeShape, bool bakeDetail )
		{
			var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>( ComputePath );
			if ( compute == null )
			{
				Debug.LogError( $"Could not load the cloud noise compute at {ComputePath}" );
				return;
			}

			try
			{
				if ( bakeShape )
				{
					BakeVolume( compute, settings, settings.shape, settings.shapeResolution,
						settings.ShapePath, perlinOnFirstChannel: true );
				}
				if ( bakeDetail )
				{
					BakeVolume( compute, settings, settings.detail, settings.detailResolution,
						settings.DetailPath, perlinOnFirstChannel: false );
				}
			}
			finally
			{
				EditorUtility.ClearProgressBar();
				// The bake overwrites the asset in place, so the cached slice would still show the old volume
				ClearPreview();
			}
		}

		static void BakeVolume(
			ComputeShader compute,
			CloudNoiseSettings settings,
			CloudNoiseSettings.WorleyChannel[] channels,
			int resolution,
			string path,
			bool perlinOnFirstChannel )
		{
			var volume = new RenderTexture( resolution, resolution, 0, GraphicsFormat.R16G16B16A16_UNorm )
			{
				volumeDepth = resolution,
				dimension = TextureDimension.Tex3D,
				enableRandomWrite = true,
				wrapMode = TextureWrapMode.Repeat,
				filterMode = FilterMode.Bilinear
			};
			volume.Create();

			int noiseKernel = compute.FindKernel( "CSNoise" );
			int normalizeKernel = compute.FindKernel( "CSNormalize" );
			compute.GetKernelThreadGroupSizes( noiseKernel, out uint groupSize, out _, out _ );
			int groups = Mathf.CeilToInt( resolution / (float)groupSize );

			compute.SetInt( "resolution", resolution );
			compute.SetInt( "perlinPeriod", Mathf.Max( 1, settings.perlinPeriod ) );
			compute.SetInt( "perlinOctaves", Mathf.Max( 1, settings.perlinOctaves ) );
			compute.SetFloat( "perlinPersistence", settings.perlinPersistence );
			compute.SetFloat( "perlinGain", settings.perlinGain );
			compute.SetTexture( noiseKernel, "Result", volume );
			compute.SetTexture( normalizeKernel, "Result", volume );

			var buffers = new List<ComputeBuffer>();

			try
			{
				for ( int channel = 0; channel < channels.Length && channel < 4; channel++ )
				{
					EditorUtility.DisplayProgressBar(
						$"Baking {System.IO.Path.GetFileNameWithoutExtension( path )}",
						$"Channel {"RGBA"[ channel ]}", channel / (float)channels.Length );

					CloudNoiseSettings.WorleyChannel worley = channels[ channel ];

					compute.SetVector( "channelMask", ChannelMask( channel ) );
					compute.SetFloat( "worleyPersistence", worley.persistence );
					compute.SetInt( "numCellsA", worley.divisionsA );
					compute.SetInt( "numCellsB", worley.divisionsB );
					compute.SetInt( "numCellsC", worley.divisionsC );
					compute.SetBool( "invertWorley", worley.invert );
					compute.SetInt( "tile", Mathf.Max( 1, worley.tile ) );
					// Only the shape volume's R channel is Perlin-Worley, as Schneider packs them
					compute.SetBool( "usePerlinWorley", perlinOnFirstChannel && channel == 0 );

					var prng = new System.Random( worley.seed );
					buffers.Add( CreatePointBuffer( compute, noiseKernel, prng, worley.divisionsA, "pointsA" ) );
					buffers.Add( CreatePointBuffer( compute, noiseKernel, prng, worley.divisionsB, "pointsB" ) );
					buffers.Add( CreatePointBuffer( compute, noiseKernel, prng, worley.divisionsC, "pointsC" ) );

					var minMax = new ComputeBuffer( 2, sizeof( int ), ComputeBufferType.Structured );
					minMax.SetData( new[] { int.MaxValue, 0 } );
					buffers.Add( minMax );

					compute.SetBuffer( noiseKernel, "minMax", minMax );
					compute.SetBuffer( normalizeKernel, "minMax", minMax );
					compute.Dispatch( noiseKernel, groups, groups, groups );
					compute.Dispatch( normalizeKernel, groups, groups, groups );
				}

				Save( volume, resolution, path );
			}
			finally
			{
				foreach ( ComputeBuffer buffer in buffers ) { buffer.Release(); }
				volume.Release();
				Object.DestroyImmediate( volume );
			}
		}

		static Vector4 ChannelMask( int channel )
		{
			Vector4 mask = Vector4.zero;
			mask[ channel ] = 1;
			return mask;
		}

		static ComputeBuffer CreatePointBuffer(
			ComputeShader compute, int kernel, System.Random prng, int cellsPerAxis, string name )
		{
			var points = new Vector3[ cellsPerAxis * cellsPerAxis * cellsPerAxis ];
			float cellSize = 1f / cellsPerAxis;

			for ( int x = 0; x < cellsPerAxis; x++ )
			{
				for ( int y = 0; y < cellsPerAxis; y++ )
				{
					for ( int z = 0; z < cellsPerAxis; z++ )
					{
						var jitter = new Vector3(
							(float)prng.NextDouble(), (float)prng.NextDouble(), (float)prng.NextDouble() ) * cellSize;
						int index = x + cellsPerAxis * ( y + z * cellsPerAxis );
						points[ index ] = new Vector3( x, y, z ) * cellSize + jitter;
					}
				}
			}

			var buffer = new ComputeBuffer( points.Length, sizeof( float ) * 3, ComputeBufferType.Structured );
			buffer.SetData( points );
			compute.SetBuffer( kernel, name, buffer );
			return buffer;
		}

		static void Save( RenderTexture volume, int resolution, string path )
		{
			int sliceBytes = resolution * resolution * 4;
			var data = new byte[ sliceBytes * resolution ];

			for ( int z = 0; z < resolution; z++ )
			{
				EditorUtility.DisplayProgressBar(
					$"Saving {System.IO.Path.GetFileNameWithoutExtension( path )}",
					$"Slice {z + 1} of {resolution}", z / (float)resolution );

				AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(
					volume, 0, 0, resolution, 0, resolution, z, 1, TextureFormat.RGBA32 );
				request.WaitForCompletion();

				if ( request.hasError )
				{
					Debug.LogError( $"Cloud noise readback failed at slice {z} for {path}" );
					return;
				}

				Unity.Collections.NativeArray<byte> slice = request.GetData<byte>();
				if ( slice.Length != sliceBytes )
				{
					Debug.LogError( $"Slice {z} returned {slice.Length} bytes, expected {sliceBytes}" );
					return;
				}

				Unity.Collections.NativeArray<byte>.Copy( slice, 0, data, z * sliceBytes, sliceBytes );
			}

			var texture = new Texture3D( resolution, resolution, resolution, TextureFormat.RGBA32, mipChain: true )
			{
				wrapMode = TextureWrapMode.Repeat,
				filterMode = FilterMode.Bilinear
			};
			texture.SetPixelData( data, 0 );
			texture.Apply( updateMipmaps: true, makeNoLongerReadable: false );

			texture.imageContentsHash = Hash128.Compute( path + System.DateTime.UtcNow.Ticks );

			System.IO.Directory.CreateDirectory( System.IO.Path.GetDirectoryName( path ) );

			var existing = AssetDatabase.LoadAssetAtPath<Texture3D>( path );
			if ( existing != null )
			{
				EditorUtility.CopySerialized( texture, existing );
				Object.DestroyImmediate( texture );
				AssetDatabase.SaveAssets();
			}
			else
			{
				AssetDatabase.CreateAsset( texture, path );
			}

			AssetDatabase.Refresh();
			Debug.Log( $"Baked {path} ({resolution}^3)" );
		}

		void DrawPreview( CloudNoiseSettings settings )
		{
			EditorGUILayout.Space();
			EditorGUILayout.LabelField( "Slice Viewer", EditorStyles.boldLabel );

			var shape = AssetDatabase.LoadAssetAtPath<Texture3D>( settings.ShapePath );
			var detail = AssetDatabase.LoadAssetAtPath<Texture3D>( settings.DetailPath );

			if ( shape == null && detail == null )
			{
				EditorGUILayout.HelpBox( "Nothing baked yet.", MessageType.Info );
				return;
			}

			using ( new EditorGUILayout.HorizontalScope() )
			{
				using ( new EditorGUI.DisabledScope( shape == null ) )
				{
					if ( GUILayout.Button( "Shape" ) ) { previewIsDetail = false; DestroyPreviewTexture(); }
				}
				using ( new EditorGUI.DisabledScope( detail == null ) )
				{
					if ( GUILayout.Button( "Detail" ) ) { previewIsDetail = true; DestroyPreviewTexture(); }
				}
			}

			Texture3D source = previewIsDetail ? detail : shape;
			if ( source == null ) { return; }

			EditorGUI.BeginChangeCheck();
			previewDepth = EditorGUILayout.Slider( "Depth", previewDepth, 0, 1 );
			previewChannel = EditorGUILayout.IntPopup(
				"Channel", previewChannel,
				new[] { "All (RGB)", "R", "G", "B", "A" },
				new[] { AllChannels, 0, 1, 2, 3 } );
			if ( EditorGUI.EndChangeCheck() ) { DestroyPreviewTexture(); }

			if ( preview == null ) { preview = BuildSlice( source ); }
			if ( preview == null ) { return; }

			Rect rect = GUILayoutUtility.GetAspectRect( 1f );
			GUI.DrawTextureWithTexCoords( rect, preview, new Rect( 0, 0, 2, 2 ) );
			EditorGUILayout.HelpBox(
				"Drawn tiled 2x2. Any visible seam across the middle means the noise does not wrap.",
				MessageType.None );
		}

		Texture2D BuildSlice( Texture3D source )
		{
			if ( !source.isReadable ) { return null; }

			if ( cachedSource != source || cachedPixels == null )
			{
				cachedPixels = source.GetPixels( 0 );
				cachedSource = source;
			}

			int size = source.width;
			int z = Mathf.Clamp( Mathf.RoundToInt( previewDepth * ( size - 1 ) ), 0, size - 1 );
			int offset = z * size * size;
			var slice = new Color[ size * size ];

			for ( int i = 0; i < slice.Length; i++ )
			{
				Color texel = cachedPixels[ offset + i ];
				slice[ i ] = previewChannel == AllChannels
					? new Color( texel.r, texel.g, texel.b, 1f )
					: new Color( texel[ previewChannel ], texel[ previewChannel ], texel[ previewChannel ], 1f );
			}

			var texture = new Texture2D( size, size, TextureFormat.RGBA32, false )
			{
				hideFlags = HideFlags.HideAndDontSave,
				wrapMode = TextureWrapMode.Repeat,
				filterMode = FilterMode.Point
			};
			texture.SetPixels( slice );
			texture.Apply();
			return texture;
		}

		void DestroyPreviewTexture()
		{
			if ( preview != null ) { DestroyImmediate( preview ); }
			preview = null;
		}

		void ClearPreview()
		{
			DestroyPreviewTexture();
			cachedPixels = null;
			cachedSource = null;
		}
	}
}
