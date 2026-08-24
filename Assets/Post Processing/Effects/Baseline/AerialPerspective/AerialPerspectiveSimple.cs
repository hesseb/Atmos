using UnityEngine;

/// <summary>
/// The baseline's aerial perspective: exponential distance fog toward a single colour.
///
/// This is the cheap counterpart to the physically based path's 3D scattering LUT, and the
/// comparison needs it. The physically based renderer does two jobs - sky radiance and
/// aerial perspective over terrain - so a baseline that only drew a sky would be one feature
/// against two, and the difference would be attributed to the sky model.
///
/// Textbook exponential fog (Real-Time Rendering, 4th ed.), which is what makes it citeable
/// as a named technique rather than an ad hoc approximation.
///
/// The one thing beyond the textbook: the haze colour is driven from a ramp keyed on sun
/// elevation, so it reddens at sunset instead of staying a fixed grey. That costs nothing at
/// run time - it is a CPU-side gradient evaluation once per frame - and it is what makes the
/// baseline credible rather than a strawman. A Gradient is also about the most artist-facing
/// authoring surface Unity has, which is itself a data point for the authoring comparison.
/// </summary>
[CreateAssetMenu(menuName = "PostProcessing/Aerial Perspective")]
public class AerialPerspectiveSimple : PostProcessingEffect
{
	[Tooltip("Scene depth, in world units, over which the fog ramps from none to full.")]
	public Vector2 depthMinMax;
	public float strength;

	[Header("Haze colour")]
	[Tooltip("Off: use the fixed colour below. On: sample the ramp by sun elevation, so the " +
		"haze tracks time of day and matches the horizon it fades into.")]
	public bool driveColourFromSunElevation = true;

	[Tooltip("Used when the ramp is off.")]
	public Color atmoCol;

	[Tooltip("Haze colour across sun elevation: t=0 is the sun 90 degrees below the horizon, " +
		"t=0.5 exactly on it, t=1 at the zenith. Author these to match the sky gradient's " +
		"horizon colours or the haze will not blend into the sky.")]
	public Gradient hazeColour;

	// Resolved lazily and cached: this is a ScriptableObject, so it cannot serialize scene
	// references.
	Transform planet;
	Light sun;

	protected override void RenderEffectToTarget(RenderTexture source, RenderTexture target)
	{
		material.SetVector("depthMinMax", depthMinMax);
		material.SetFloat("strength", strength);
		material.SetColor("atmoCol", CurrentHazeColour());

		Graphics.Blit(source, target, material);
	}

	Color CurrentHazeColour()
	{
		if (!driveColourFromSunElevation || cam == null) { return atmoCol; }

		EnsureGradient();

		Vector3 planetCentre = ObserverGeometry.PlanetCentre(ref planet);
		Vector3 dirToSun = ObserverGeometry.DirectionToSun(ref sun);
		float sunElevation01 = ObserverGeometry.SunElevation01(
			cam.transform.position, planetCentre, dirToSun);

		return hazeColour.Evaluate(sunElevation01);
	}

	/// <summary>
	/// Fills in a sensible ramp the first time, so the effect does not silently fade the world
	/// to Unity's default black-to-white gradient. These match the sky gradient's horizon
	/// anchors in SkyGradientTools - the haze has to blend into the horizon it sits against.
	///
	/// Unlike the sky, these are NOT inverse-tone-mapped: this pass runs after the sky and
	/// applies no tone map of its own, so the colour is used as-is.
	/// </summary>
	void EnsureGradient()
	{
		if (hazeColour != null && hazeColour.colorKeys != null && hazeColour.colorKeys.Length > 1)
		{
			return;
		}

		hazeColour = new Gradient();
		hazeColour.SetKeys(
			new[]
			{
				new GradientColorKey(new Color(0.035f, 0.045f, 0.075f), 0.000f),   // -90 deg
				new GradientColorKey(new Color(0.100f, 0.110f, 0.170f), 0.433f),   // -12
				new GradientColorKey(new Color(0.380f, 0.240f, 0.220f), 0.478f),   //  -4
				new GradientColorKey(new Color(0.950f, 0.470f, 0.220f), 0.500f),   //   0
				new GradientColorKey(new Color(0.920f, 0.740f, 0.560f), 0.556f),   //  10
				new GradientColorKey(new Color(0.720f, 0.820f, 0.940f), 0.667f),   //  30
				new GradientColorKey(new Color(0.700f, 0.810f, 0.950f), 1.000f)    //  90
			},
			new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
	}
}
