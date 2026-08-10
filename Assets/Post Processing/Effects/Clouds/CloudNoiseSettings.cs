using UnityEngine;

namespace Clouds
{
	/// <summary>
	/// Parameters for the two 3D noise volumes the cloud density model samples, and the identity of
	/// the assets they bake to.
	///
	/// An asset rather than fields on a scene component, so the tuning is versioned and survives
	/// play-mode changes - and so that "how many parameters does this technique expose, and how
	/// predictably does each one map to a visible outcome" is answerable by looking at one file.
	/// RQ3 grades authoring flexibility, and that is evidence.
	///
	/// Bake with the button on this asset's inspector. See CloudNoiseGen.compute for what the
	/// channels hold and why everything has to tile.
	/// </summary>
	[CreateAssetMenu(menuName = "Clouds/Noise Settings", fileName = "Cloud Noise Settings")]
	public class CloudNoiseSettings : ScriptableObject
	{
		/// <summary>
		/// One channel's worth of Worley FBM: three octaves of a jittered grid, combined by
		/// persistence. Divisions are cells per axis, so larger means finer detail.
		/// </summary>
		[System.Serializable]
		public struct WorleyChannel
		{
			public int seed;
			[Range(1, 50)] public int divisionsA;
			[Range(1, 50)] public int divisionsB;
			[Range(1, 50)] public int divisionsC;
			[Range(0, 1)] public float persistence;
			[Range(1, 4)] public int tile;
			public bool invert;

			public static WorleyChannel Create(int seed, int a, int b, int c, float persistence)
			{
				return new WorleyChannel
				{
					seed = seed,
					divisionsA = a,
					divisionsB = b,
					divisionsC = c,
					persistence = persistence,
					tile = 1,
					invert = true
				};
			}
		}

		[Header("Resolution")]
		[Tooltip("Schneider uses 128. Cost is cubic, and this volume is sampled at every march step.")]
		public int shapeResolution = 128;
		public int detailResolution = 32;

		[Header("Shape volume (128^3 RGBA)")]
		[Tooltip("R is the Perlin-Worley base; G, B and A are Worley at increasing frequency. " +
			"Frequencies default to the reference project's, which are known to work.")]
		public WorleyChannel[] shape = new WorleyChannel[4]
		{
			WorleyChannel.Create(0, 3, 7, 11, 0.65f),
			WorleyChannel.Create(1, 9, 15, 23, 0.33f),
			WorleyChannel.Create(2, 13, 28, 42, 0.58f),
			WorleyChannel.Create(3, 20, 31, 45, 0.74f),
		};

		[Header("Detail volume (32^3 RGB)")]
		public WorleyChannel[] detail = new WorleyChannel[3]
		{
			WorleyChannel.Create(4, 8, 18, 20, 0.76f),
			WorleyChannel.Create(5, 13, 24, 28, 0.60f),
			WorleyChannel.Create(6, 20, 28, 32, 0.60f),
		};

		[Header("Perlin (shape channel R only)")]
		[Tooltip("Lattice cells per axis at the first octave. Must divide the resolution evenly to " +
			"tile cleanly, and doubles per octave.")]
		[Range(1, 16)] public int perlinPeriod = 4;
		[Range(1, 8)] public int perlinOctaves = 6;
		[Range(0, 1)] public float perlinPersistence = 0.5f;
		[Tooltip("Stretches the FBM to fill [-1,1] before folding it to [0,1]. An FBM normalised by " +
			"the sum of its amplitudes does not reach that sum - measured over these defaults it spans " +
			"only about +/-0.45, which would leave the Perlin unable to pull the remap down to the " +
			"Worley floor. Retune if octaves or persistence change; flat patches in the slice viewer " +
			"mean it is too high.")]
		[Range(1, 4)] public float perlinGain = 2.2f;

		[Header("Output")]
		public string outputFolder = "Assets/Data/Clouds";
		public string shapeAssetName = "Cloud Shape Noise";
		public string detailAssetName = "Cloud Detail Noise";

		public string ShapePath => $"{outputFolder}/{shapeAssetName}.asset";
		public string DetailPath => $"{outputFolder}/{detailAssetName}.asset";
	}
}
