using UnityEngine;

namespace TerrainGeneration
{
	[CreateAssetMenu(menuName = "Terrain/Height Settings")]
	public class TerrainHeightSettings : ScriptableObject
	{
		public float worldRadius = 150;
		// Deliberately not part of the world-scale presets, which swap at runtime.
		//
		// Changing it means regenerating the terrain meshes, which a keypress should not do. The
		// consequence is that 3 units is 0.34 Rayleigh scale heights at the 136 km scale and 1.88
		// at the 750 km one, so mountains stand out of the haze on the larger planet. That is
		// left visible on purpose: it is part of what makes that scale impractical.
		public float heightMultiplier = 3f;
	}

}