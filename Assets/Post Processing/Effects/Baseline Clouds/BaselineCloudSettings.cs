using UnityEngine;

namespace Clouds
{
	/// <summary>
	/// Parameters for the baseline cloud layers, and the identity of the textures they bake to.
	///
	/// These are the authoring surface for the simple method, and that makes them evidence as well as
	/// configuration: RQ3 grades authoring flexibility, and "how many parameters does each technique
	/// expose, and how predictably does each map to a visible outcome" is answered by comparing this
	/// file against CloudEffect's inspector. Keeping them on one asset is deliberate for that reason.
	/// </summary>
	[CreateAssetMenu(menuName = "Clouds/Baseline Cloud Settings", fileName = "Baseline Cloud Settings")]
	public class BaselineCloudSettings : ScriptableObject
	{
		/// <summary>
		/// One cloud layer. Two of them are stacked at different radii so they parallax against each
		/// other, which is the depth cue a single flat overlay cannot give on a globe.
		/// </summary>
		[System.Serializable]
		public struct Layer
		{
			[Tooltip("Feature size of the cloud shapes themselves. Larger means smaller, more " +
				"numerous clouds.")]
			[Range(0.5f, 40f)] public float scale;

			[Tooltip("Feature size of the coverage field, which decides WHERE there is cloud at all. " +
				"Much lower than the shape scale: without a separate large-scale field the layer has " +
				"the same character everywhere, which is the uniformity the volumetric's weather map " +
				"exists to avoid - and handing the baseline that flaw would not be a fair comparison.")]
			[Range(0.2f, 12f)] public float coverageScale;

			[Tooltip("The main how-cloudy-is-it dial.")]
			[Range(-0.5f, 0.5f)] public float bias;

			[Tooltip("Broken versus overcast.")]
			[Range(0.2f, 4f)] public float contrast;

			[Range(1, 8)] public int octaves;
			[Range(1.2f, 4f)] public float lacunarity;
			[Range(0.1f, 0.9f)] public float gain;

			[Tooltip("Offsets the field so the two layers are not the same clouds twice.")]
			public float seedOffset;

			[Tooltip("How pronounced the relief shading is. This is the height field's amplitude, so " +
				"it drives the surface normal rather than the density.")]
			[Range(0f, 4f)] public float reliefStrength;

			public static Layer Create(float scale, float bias, float seedOffset)
			{
				return new Layer
				{
					scale = scale,
					coverageScale = 2.2f,
					bias = bias,
					contrast = 1.4f,
					octaves = 6,
					lacunarity = 2f,
					gain = 0.5f,
					seedOffset = seedOffset,
					reliefStrength = 1.5f
				};
			}
		}

		[Header("Resolution")]
		[Tooltip("Higher than the weather map because this IS the cloud rather than a field describing " +
			"one - it is sampled directly at the shell and its detail is what the viewer sees.")]
		public int resolution = 2048;

		[Header("Layers")]
		[Tooltip("The lower, denser layer.")]
		public Layer lower = Layer.Create(14f, 0.1f, 0f);

		[Tooltip("The upper, thinner layer. Coarser and sparser so it reads as higher cloud rather " +
			"than as a second copy of the same weather.")]
		public Layer upper = Layer.Create(7f, 0f, 91.3f);

		[Header("Range correction")]
		[Tooltip("Stretches the field to fill its range before folding to 0-1. An FBM normalised by " +
			"the sum of its amplitudes never reaches that sum, so without this every layer sits in " +
			"the middle third of its range. The same correction the volumetric's weather map and its " +
			"Perlin both need, and the default is measured the same way.")]
		[Range(1f, 4f)] public float rangeGain = 2.3f;

		[Header("Output")]
		public string outputFolder = "Assets/Data/Baseline Clouds";

		public string AuthoredPath(bool upperLayer) =>
			$"{outputFolder}/Cloud Layer {(upperLayer ? "Upper" : "Lower")} Authored.png";

		/// <summary>
		/// Baked off the volumetric renderer rather than authored. Same format and same cost to
		/// sample, so any visual difference against the authored pair is the art rather than the
		/// technique - the separation the baseline sky's GradientBaked variant exists to provide.
		/// </summary>
		public string BakedPath(bool upperLayer) =>
			$"{outputFolder}/Cloud Layer {(upperLayer ? "Upper" : "Lower")} Baked.png";
	}
}
