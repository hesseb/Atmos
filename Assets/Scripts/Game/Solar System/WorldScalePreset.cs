using UnityEngine;

/// <summary>
/// A world scale: how many kilometres a world unit is worth, and the parameters that follow.
///
/// The globe is fixed at radius 150 world units, so the planet's size *in kilometres* is set
/// entirely by the atmosphere column - declare the column to be Earth's 100 km and one world
/// unit becomes 100/thickness km. Nothing in world units moves, which is what makes swapping
/// this at runtime possible at all.
///
/// Two presets exist because the choice is a genuine trade rather than a bug to fix, and the
/// trade is a thesis result in its own right:
///
///   - A **136 km** planet is playable. Enough of the globe is visible at once, mountains read
///     correctly against the haze, and the camera sits comfortably inside the air.
///   - A **750 km** planet is what the physics wants. Horizon air mass rises from 5.2 to 12.1,
///     so the slant path at sunset is long enough to actually redden without so much help from
///     the density multiplier. It is also close to unplayable: you see a small patch of the
///     globe, and mountains stand out of the haze layer.
///
/// Both match Earth's *horizon* optical depth in blue - the quantity a sunset is made of - by
/// leaning on `densityMultiplier` to whatever degree the geometry does not supply. That is the
/// number the comparison is about: 6.8x on the small planet against 2.9x on the large one.
/// </summary>
[CreateAssetMenu(menuName = "Testbed/World Scale Preset")]
public class WorldScalePreset : ScriptableObject
{
	[Tooltip("Short name shown in the HUD.")]
	public string id = "scale";

	[TextArea(2, 5)]
	public string description;

	[Header("Planet")]
	/// <summary>
	/// Uniform scale applied to the World root, and to every radius derived from it.
	///
	/// This is the honest planet-size dial: it moves the actual geometry, so the globe really is
	/// bigger and the horizon really is flatter. The atmosphere's scale height stays fixed in
	/// world units, so R/H - the only thing horizon air mass depends on - grows with it, and air
	/// mass grows as its square root.
	///
	/// Free at runtime because the terrain is not generated at play time: `LodMeshLoader`
	/// deserialises pre-baked meshes, so there is nothing to recompute and both scales are the
	/// *same* terrain, which makes the comparison controlled rather than two separate bakes.
	///
	/// **Caveat worth stating in the report.** A uniform scale scales the relief too, so at 4x
	/// the mountains are 12 world units rather than 3 against an unchanged 8.8-unit scale height.
	/// A real planet four times larger would not have four times taller mountains, so this
	/// exaggerates how far peaks stand out of the haze. Fixing that properly means re-baking the
	/// terrain at a different worldRadius with the same heightMultiplier - the offline generator
	/// can do it, at the cost of a second copy of the mesh data.
	/// </summary>
	public float planetScale = 1;

	[Header("Atmosphere")]
	[Tooltip("Column depth in world units. The planet's radius in kilometres is " +
		"150 * 100 / this, since the column is defined to be Earth's 100 km.")]
	public float atmosphereThickness = 110;

	[Tooltip("Multiplies every scattering and absorption coefficient. Set so the horizon " +
		"optical depth in blue matches Earth's, which the geometry alone cannot deliver.")]
	public float densityMultiplier = 1;

	[Header("Tone mapping")]
	[Tooltip("Vertical optical depth differs several-fold between presets, so the display " +
		"transform cannot be shared.")]
	public float intensity = 1.602f;
	public float contrast = 1.45f;
	public float whitePoint = 2.5f;

	[Header("Camera")]
	[Tooltip("Altitude to move the camera to, in world units. Pan and fly speeds are absolute " +
		"rates, so they have to move with it or the same motion sweeps a different fraction " +
		"of the view.")]
	public float altitude = 10;
	public float referenceAltitude = 10;

	[Tooltip("Minimum altitude a camera mode switch lands at, before the planet scale is " +
		"applied. Bigger than `altitude` on purpose - it exists to orient from, not to fly at.")]
	public float modeSwitchAltitude = 40;
	public float panSpeed = 0.35f;
	public float flySpeed = 20;

	/// <summary>Kilometres per world unit, from the 100 km column definition.</summary>
	public float KilometresPerUnit => 100f / Mathf.Max(1e-4f, atmosphereThickness);

	/// <summary>Planet radius in kilometres, for a globe of the given world-unit radius.</summary>
	public float PlanetRadiusKm(float bodyRadius) => bodyRadius * planetScale * KilometresPerUnit;

	/// <summary>
	/// Chapman horizon air mass, sqrt(pi*R / 2H) - the amplification of the slant path to the
	/// horizon over the vertical column, and the single number that decides whether a sunset is
	/// geometrically possible. Earth's is 35.4.
	/// </summary>
	public float HorizonAirMass(float bodyRadius, float rayleighDensityAvg)
	{
		float scaleHeight = Mathf.Max(1e-4f, rayleighDensityAvg * atmosphereThickness);
		return Mathf.Sqrt(Mathf.PI * bodyRadius * planetScale / (2f * scaleHeight));
	}
}
