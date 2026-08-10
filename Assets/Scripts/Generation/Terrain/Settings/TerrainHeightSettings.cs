using UnityEngine;

namespace TerrainGeneration
{
	[CreateAssetMenu(menuName = "Terrain/Height Settings")]
	public class TerrainHeightSettings : ScriptableObject
	{
		public float worldRadius = 150;
		// 1.10 Rayleigh scale heights at the 750 km scale, which is Earth's ratio of peak height
		// to scale height. Above that, mountains start poking out of the atmosphere.
		public float heightMultiplier = 1.76f;
	}

}