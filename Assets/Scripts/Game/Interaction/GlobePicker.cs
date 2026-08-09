using UnityEngine;

/// <summary>
/// Resolves which country is under the mouse cursor.
///
/// There are no colliders in the scene, so picking is an analytic ray/sphere test against
/// the globe followed by a country-index lookup through <see cref="WorldLookup"/>.
///
/// Runs in LateUpdate with an execution order after TestbedCamera, which applies its pose
/// there - picking in Update would use the previous frame's camera matrix.
///
/// MEASUREMENT NOTE: turn <see cref="showOverlay"/> off before timing anything.
/// </summary>
[DefaultExecutionOrder(100)]
public class GlobePicker : MonoBehaviour
{
	[Header("References")]
	public Camera cam;
	public WorldLookup worldLookup;
	public CountryData countryData;
	public TerrainGeneration.TerrainHeightSettings heightSettings;

	[Header("Picking")]
	// Terrain spans worldRadius .. worldRadius + heightMultiplier. Most land is low, so
	// start near the bottom of that range; the radius then self-corrects (see OnResult).
	[Range(0f, 1f)] public float initialHeightFraction = 0.35f;
	public bool adaptRadiusToTerrain = true;

	[Header("Debug")]
	public bool showOverlay;

	public int HoveredCountryIndex { get; private set; } = -1;

	public Country HoveredCountry
	{
		get
		{
			if (countryData == null || HoveredCountryIndex < 0) { return null; }
			Country[] all = countryData.Countries;
			return (HoveredCountryIndex < all.Length) ? all[HoveredCountryIndex] : null;
		}
	}

	public event System.Action<int> onHoveredCountryChanged;

	float pickRadius;
	bool hasPicked;
	bool resampleRequested;
	Vector3 lastMousePosition;
	Vector3 lastCamPosition;
	Quaternion lastCamRotation;
	System.Action<TerrainInfo> onResult;
	GUIStyle overlayStyle;

	float DefaultPickRadius
	{
		get
		{
			if (heightSettings == null) { return 150f; }
			return heightSettings.worldRadius + heightSettings.heightMultiplier * initialHeightFraction;
		}
	}

	void OnEnable()
	{
		if (cam == null) { cam = Camera.main; }
		if (worldLookup == null) { worldLookup = FindObjectOfType<WorldLookup>(); }

		pickRadius = DefaultPickRadius;
		onResult = OnResult;
		hasPicked = false;
	}

	void LateUpdate()
	{
		if (cam == null || worldLookup == null) { return; }

		Vector3 mouse = Input.mousePosition;
		if (mouse.x < 0f || mouse.y < 0f || mouse.x >= Screen.width || mouse.y >= Screen.height)
		{
			// Input.mousePosition keeps reporting when the pointer leaves the game view.
			SetHovered(-1);
			hasPicked = false;
			return;
		}

		Transform camT = cam.transform;
		bool viewChanged = mouse != lastMousePosition
			|| camT.position != lastCamPosition
			|| camT.rotation != lastCamRotation;

		if (!viewChanged && hasPicked && !resampleRequested) { return; }
		// One query in flight at a time - the result then always belongs to the latest ray.
		if (worldLookup.RequestPending) { return; }

		lastMousePosition = mouse;
		lastCamPosition = camT.position;
		lastCamRotation = camT.rotation;
		resampleRequested = false;
		hasPicked = true;

		if (!TryRaycastGlobe(mouse, pickRadius, out Vector3 hit))
		{
			SetHovered(-1);
			return;
		}

		worldLookup.GetTerrainInfoAsync(hit, onResult);
	}

	/// <summary>
	/// Ray against a sphere of the given radius centred on the origin. Handles the camera
	/// being inside the sphere, which free-fly mode allows.
	/// </summary>
	public bool TryRaycastGlobe(Vector3 screenPosition, float radius, out Vector3 hitPoint)
	{
		hitPoint = Vector3.zero;

		Ray ray = cam.ScreenPointToRay(screenPosition);
		Vector3 o = ray.origin;
		Vector3 d = ray.direction;

		float b = Vector3.Dot(o, d);
		float c = Vector3.Dot(o, o) - radius * radius;
		float discriminant = b * b - c;
		if (discriminant < 0f) { return false; }

		float sqrtDisc = Mathf.Sqrt(discriminant);
		float t = -b - sqrtDisc;
		if (t < 0f) { t = -b + sqrtDisc; }
		if (t < 0f) { return false; }

		hitPoint = o + d * t;
		return true;
	}

	void OnResult(TerrainInfo info)
	{
		if (adaptRadiusToTerrain)
		{
			// The lookup returns the actual terrain height at the point we hit, which is a
			// better sphere radius than the guess we used. Re-pick once if it moved
			// meaningfully - this matters at grazing angles near the horizon, where a
			// wrong radius displaces the hit point a long way along the surface.
			if (Mathf.Abs(info.height - pickRadius) > 0.05f)
			{
				resampleRequested = true;
			}
			pickRadius = info.height;
		}

		SetHovered(info.inOcean ? -1 : info.countryIndex);
	}

	void SetHovered(int index)
	{
		if (index == HoveredCountryIndex) { return; }

		HoveredCountryIndex = index;
		if (index < 0 && adaptRadiusToTerrain) { pickRadius = DefaultPickRadius; }
		onHoveredCountryChanged?.Invoke(index);
	}

	void OnGUI()
	{
		if (!showOverlay) { return; }

		if (overlayStyle == null)
		{
			overlayStyle = new GUIStyle { richText = false, wordWrap = false };
			overlayStyle.padding = new RectOffset(10, 10, 8, 8);
		}
		overlayStyle.fontSize = 16;
		overlayStyle.normal.textColor = Color.white;

		Country hovered = HoveredCountry;
		string text = hovered == null
			? $"hover: ocean / none   (index {HoveredCountryIndex})"
			: $"hover: {hovered.GetPreferredDisplayName()}   [{HoveredCountryIndex}] {hovered.alpha3Code}";
		text += $"\npick radius {pickRadius:0.00}";

		GUIContent content = new GUIContent(text);
		Vector2 size = overlayStyle.CalcSize(content);
		GUI.Label(new Rect(10f, Screen.height - size.y - 10f, size.x, size.y), content, overlayStyle);
	}
}
