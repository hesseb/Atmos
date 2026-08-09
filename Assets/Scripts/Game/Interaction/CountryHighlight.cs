using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Draws a glowing border around the country under the cursor.
///
/// The baked outline mesh (Outline Meshes.bytes) carries no country identity - it is 24
/// spatially grouped meshes and the country each border belonged to is discarded during
/// generation - so the highlight cannot tint existing geometry. It builds its own line
/// geometry from Country.shape instead, for one country at a time.
///
/// Drawn with ZTest Always rather than depth-tested against the terrain. Terrain spans
/// worldRadius..worldRadius+heightMultiplier (150..153.5), so no single radius avoids
/// intersecting mountains, and lifting the lines above the peaks introduces visible
/// parallax against the ground. Drawing on top and culling beyond the horizon in the
/// vertex shader avoids both problems.
///
/// MEASUREMENT NOTE: this issues two indirect draws per frame while a country is hovered.
/// Disable the component for timing runs.
/// </summary>
[DefaultExecutionOrder(101)]
public class CountryHighlight : MonoBehaviour
{
	[Header("References")]
	public GlobePicker picker;
	public CountryData countryData;
	public TerrainGeneration.TerrainHeightSettings heightSettings;
	// Supplies the terrain height texture the shaders use to sit the glow on the ground
	// rather than at sea level. Optional - without it the glow stays at sea level and
	// drifts off the drawn borders at shallow camera angles.
	public WorldLookup worldLookup;
	public Shader lineShader;
	public Shader joinShader;

	[Header("Appearance")]
	// Bright core.
	public Color colour = DefaultColour;
	// Darker surround. A single warm line is nearly invisible over pale desert, so the
	// halo is what guarantees an edge against whatever terrain is underneath.
	public Color haloColour = DefaultHaloColour;
	// Total line width in pixels of screen height, halo included.
	public float widthPixels = DefaultWidthPixels;
	// Fraction of the width held at the core colour before the halo takes over. Below
	// about 0.5 the bright core thins to nothing and only the dark halo is left.
	[Range(0.2f, 1f)] public float coreFraction = DefaultCoreFraction;
	// Softness of the core-to-halo transition. Higher = softer, glowier.
	[Range(0.05f, 1f)] public float edgeSoftness = DefaultEdgeSoftness;
	public float fadeInDuration = 0.12f;

	static Color DefaultColour => new Color(1f, 0.97f, 0.85f, 1f);
	static Color DefaultHaloColour => new Color(0.06f, 0.03f, 0f, 0.7f);
	const float DefaultWidthPixels = 8f;
	const float DefaultCoreFraction = 0.55f;
	const float DefaultEdgeSoftness = 0.4f;
	[Range(3, 24)] public int joinResolution = 8;
	public bool drawJoins = true;

	[Header("Rendering")]
	// Layer 5 (UI) - unused by the scene, inside the camera's culling mask, and not
	// subject to the 400-unit cull distance that layer 6 (Earth) has.
	public int layer = 5;
	// Leave null to draw to every camera including the Scene view.
	public Camera targetCamera;

	ComputeBuffer segmentsBuffer;
	ComputeBuffer lineArgsBuffer;
	ComputeBuffer joinArgsBuffer;
	Mesh segmentMesh;
	Mesh joinMesh;
	Material lineMaterial;
	Material joinMaterial;

	readonly List<LineSegment> scratch = new List<LineSegment>();
	readonly uint[] args = new uint[5];
	int activeSegments;
	float fade;
	Bounds bounds;
	bool initialised;

	float GlobeRadius => heightSettings != null ? heightSettings.worldRadius : 150f;

	void OnEnable()
	{
		if (picker == null) { picker = GetComponent<GlobePicker>(); }
		if (worldLookup == null) { worldLookup = FindObjectOfType<WorldLookup>(); }
		Initialise();

		if (picker != null)
		{
			picker.onHoveredCountryChanged += OnHoveredCountryChanged;
			OnHoveredCountryChanged(picker.HoveredCountryIndex);
		}
	}

	void OnDisable()
	{
		if (picker != null) { picker.onHoveredCountryChanged -= OnHoveredCountryChanged; }
		activeSegments = 0;
	}

	void Initialise()
	{
		if (initialised) { return; }

		if (countryData == null || lineShader == null || joinShader == null)
		{
			Debug.LogWarning($"{nameof(CountryHighlight)}: assign countryData, lineShader and " +
				"joinShader - no highlight will be drawn until then.", this);
			return;
		}

		segmentMesh = LineMeshUtility.CreateLineSegmentMesh();
		joinMesh = LineMeshUtility.CreateCircleJoinMesh(joinResolution);

		lineMaterial = new Material(lineShader) { hideFlags = HideFlags.HideAndDontSave };
		joinMaterial = new Material(joinShader) { hideFlags = HideFlags.HideAndDontSave };

		// Allocated once at worst-case size so hover changes never reallocate.
		int maxSegments = Mathf.Max(1, MaxSegmentCount());
		ComputeHelper.CreateStructuredBuffer<LineSegment>(ref segmentsBuffer, maxSegments);
		lineMaterial.SetBuffer("lineSegments", segmentsBuffer);
		joinMaterial.SetBuffer("lineSegments", segmentsBuffer);

		lineArgsBuffer = ComputeHelper.CreateArgsBuffer(segmentMesh, 0);
		joinArgsBuffer = ComputeHelper.CreateArgsBuffer(joinMesh, 0);

		initialised = true;
	}

	int MaxSegmentCount()
	{
		int max = 0;
		Country[] countries = countryData.Countries;
		for (int i = 0; i < countries.Length; i++)
		{
			max = Mathf.Max(max, CountSegments(countries[i]));
		}
		return max;
	}

	static int CountSegments(Country country)
	{
		if (country == null || country.shape.polygons == null) { return 0; }

		int count = 0;
		foreach (Polygon polygon in country.shape.polygons)
		{
			if (polygon.paths == null) { continue; }
			foreach (Path path in polygon.paths)
			{
				if (path.points != null && path.points.Length >= 2)
				{
					count += path.points.Length - 1;
				}
			}
		}
		return count;
	}

	void OnHoveredCountryChanged(int index)
	{
		if (!initialised) { return; }

		activeSegments = 0;
		fade = 0f;

		if (index < 0) { return; }

		Country[] countries = countryData.Countries;
		if (index >= countries.Length) { return; }

		BuildSegments(countries[index]);
		if (scratch.Count == 0) { return; }

		segmentsBuffer.SetData(scratch, 0, 0, scratch.Count);
		activeSegments = scratch.Count;

		SetInstanceCount(lineArgsBuffer, segmentMesh, activeSegments);
		SetInstanceCount(joinArgsBuffer, joinMesh, activeSegments);
	}

	/// <summary>
	/// Converts a country's polygons into 3D line segments on the globe.
	///
	/// Includes holes, not just the outer ring - a hole is a border too (Lesotho inside
	/// South Africa). Building in 3D also means the antimeridian needs no special case:
	/// +179.9 and -179.9 degrees become nearby points and the chord between them is short
	/// and correct, where a lon/lat-space builder would draw a stripe across the map.
	/// </summary>
	void BuildSegments(Country country)
	{
		scratch.Clear();
		if (country == null || country.shape.polygons == null) { return; }

		float radius = GlobeRadius;
		Vector3 min = Vector3.positiveInfinity;
		Vector3 max = Vector3.negativeInfinity;

		foreach (Polygon polygon in country.shape.polygons)
		{
			if (polygon.paths == null) { continue; }

			foreach (Path path in polygon.paths)
			{
				Coordinate[] points = path.points;
				if (points == null || points.Length < 2) { continue; }

				Vector3 previous = GeoMaths.CoordinateToPoint(points[0], radius);
				for (int i = 1; i < points.Length; i++)
				{
					Vector3 current = GeoMaths.CoordinateToPoint(points[i], radius);
					scratch.Add(new LineSegment { pointA = previous, pointB = current });

					min = Vector3.Min(min, current);
					max = Vector3.Max(max, current);
					previous = current;
				}
				min = Vector3.Min(min, GeoMaths.CoordinateToPoint(points[0], radius));
				max = Vector3.Max(max, GeoMaths.CoordinateToPoint(points[0], radius));
			}
		}

		// Padded so screen-space widening at the edges can't clip the bounds.
		bounds = scratch.Count > 0
			? new Bounds((min + max) * 0.5f, (max - min) + Vector3.one * 2f)
			: new Bounds(Vector3.zero, Vector3.one);
	}

	void SetInstanceCount(ComputeBuffer argsBuffer, Mesh mesh, int instances)
	{
		args[0] = mesh.GetIndexCount(0);
		args[1] = (uint)instances;
		args[2] = mesh.GetIndexStart(0);
		args[3] = mesh.GetBaseVertex(0);
		args[4] = 0;
		argsBuffer.SetData(args);
	}

	void LateUpdate()
	{
		if (!initialised || activeSegments == 0) { return; }

		fade = fadeInDuration > 0f
			? Mathf.MoveTowards(fade, 1f, Time.unscaledDeltaTime / fadeInDuration)
			: 1f;

		float width = widthPixels / Mathf.Max(1f, Screen.height);
		float radius = GlobeRadius;

		ApplyMaterial(lineMaterial, fade, width, radius);
		Graphics.DrawMeshInstancedIndirect(segmentMesh, 0, lineMaterial, bounds, lineArgsBuffer,
			0, null, ShadowCastingMode.Off, false, layer, targetCamera);

		if (drawJoins)
		{
			ApplyMaterial(joinMaterial, fade, width, radius);
			Graphics.DrawMeshInstancedIndirect(joinMesh, 0, joinMaterial, bounds, joinArgsBuffer,
				0, null, ShadowCastingMode.Off, false, layer, targetCamera);
		}
	}

	void ApplyMaterial(Material material, float fadeAlpha, float width, float radius)
	{
		// Fade multiplies both layers so the halo doesn't linger after the core.
		Color core = colour;
		core.a *= fadeAlpha;
		Color halo = haloColour;
		halo.a *= fadeAlpha;

		material.SetColor("colour", core);
		material.SetColor("haloColour", halo);
		material.SetFloat("width", width);
		material.SetFloat("globeRadius", radius);
		material.SetFloat("coreFraction", Mathf.Clamp(coreFraction, 0.02f, 1f));
		// smoothstep(edge, edge, x) is degenerate, so keep the two edges apart.
		material.SetFloat("edgeSoftness", Mathf.Clamp(edgeSoftness, 0.05f, 0.99f));

		// Height texture is created during loading, so it can't be bound once at init.
		// A zero multiplier keeps the glow at sea level if it isn't available.
		RenderTexture heightMap = worldLookup != null ? worldLookup.HeightLookup : null;
		if (heightMap != null)
		{
			material.SetTexture("HeightMap", heightMap);
			material.SetFloat("heightMultiplier",
				heightSettings != null ? heightSettings.heightMultiplier : 0f);
		}
		else
		{
			material.SetFloat("heightMultiplier", 0f);
		}
	}

	void OnDestroy()
	{
		ComputeHelper.Release(segmentsBuffer, lineArgsBuffer, joinArgsBuffer);

		DestroyObject(lineMaterial);
		DestroyObject(joinMaterial);
		DestroyObject(segmentMesh);
		DestroyObject(joinMesh);
	}

	static void DestroyObject(Object obj)
	{
		if (obj == null) { return; }
		if (Application.isPlaying) { Destroy(obj); }
		else { DestroyImmediate(obj); }
	}

	/// <summary>
	/// Reapplies the appearance defaults without touching the reference fields.
	///
	/// Unity keeps serialized values when a script's defaults change, so an instance
	/// placed in the scene before a retune keeps the old numbers - which is how a width
	/// tuned for one shading model ends up driving a different one. Component > Reset
	/// would fix that too, but would also clear countryData and the shaders.
	/// </summary>
	[ContextMenu("Reset Appearance")]
	void ResetAppearance()
	{
		colour = DefaultColour;
		haloColour = DefaultHaloColour;
		widthPixels = DefaultWidthPixels;
		coreFraction = DefaultCoreFraction;
		edgeSoftness = DefaultEdgeSoftness;
	}
}
