using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Draws country names on the globe, grand-strategy style: text lying flat on the
/// surface at each country's baked anchor, oriented to local north, sized by how large
/// the country actually is, and faded out once it is too small on screen or has rotated
/// past the horizon.
///
/// All labels are created once and then only toggled and faded. Text generation is the
/// expensive part, so pooling would re-trigger it on every recycle; 241 inactive
/// TextMeshPro components cost almost nothing by comparison.
///
/// MEASUREMENT NOTE: this updates every label every frame. Disable the component (or its
/// GameObject) for timing runs.
/// </summary>
[DefaultExecutionOrder(102)]
public class CountryLabelSystem : MonoBehaviour
{
	[Header("References")]
	public Camera cam;
	public CountryData countryData;
	public CountryLabelData labelData;
	public TerrainGeneration.TerrainHeightSettings heightSettings;
	public TMP_FontAsset fontAsset;
	// Optional. Left empty, a material is built from the font asset with the TMP overlay
	// shader, which is already ZTest Always / ZWrite Off / Queue Overlay.
	public Material labelMaterial;

	[Header("Placement")]
	// Layer 5 (UI) - unused by the scene, in the camera's culling mask, and free of the
	// 400-unit cull distance that layer 6 (Earth) carries.
	public int layer = 5;
	public Color textColour = Color.white;
	// Fraction of the country's inscribed circle the text is sized to span.
	[Range(0.1f, 2f)] public float fitFactor = 0.9f;
	public float minScale = 0.02f;
	public float maxScale = 4f;

	[Header("Visibility")]
	// Text shorter than this on screen is faded out; the band above it is the fade-in.
	public float minPixelHeight = 9f;
	public float fadeBandFactor = 1.6f;
	// Fades as the label rotates toward the limb, where flat text is foreshortened into
	// an unreadable smear well before it actually disappears.
	[Range(0f, 1f)] public float horizonFadeStart = 0.15f;
	[Range(0f, 1f)] public float horizonFadeEnd = 0.35f;
	// Labels whose anchor fell back to a centroid may sit outside their country.
	public bool showFailedBakes = false;

	[Header("Debug")]
	public bool logInitTime = true;
	// Escape hatch in case the text renders mirrored on some platform.
	public bool flipFacing;

	class Label
	{
		public TextMeshPro text;
		public Transform transform;
		public GameObject gameObject;
		public Vector3 anchorDirection;
		public Vector3 worldPosition;
		public float worldHeight;   // of the text, in world units
		public float alpha;
		public bool active;
	}

	readonly List<Label> labels = new List<Label>();
	Material runtimeMaterial;
	Transform container;
	bool initialised;

	float GlobeRadius => heightSettings != null ? heightSettings.worldRadius : 150f;

	public int VisibleCount { get; private set; }

	void Start()
	{
		Initialise();
	}

	void Initialise()
	{
		if (initialised) { return; }

		if (cam == null) { cam = Camera.main; }
		if (countryData == null || labelData == null || fontAsset == null)
		{
			Debug.LogWarning($"{nameof(CountryLabelSystem)}: assign countryData, labelData and " +
				"fontAsset - no labels will be created.", this);
			return;
		}

		if (!labelData.ValidateAlignment(countryData, out string error))
		{
			Debug.LogError($"{nameof(CountryLabelSystem)}: {error}", this);
			return;
		}

		var timer = System.Diagnostics.Stopwatch.StartNew();

		Material material = labelMaterial;
		if (material == null)
		{
			material = CreateOverlayMaterial();
			if (material == null) { return; }
		}

		container = new GameObject("Labels").transform;
		container.SetParent(transform, false);

		float radius = GlobeRadius;
		for (int i = 0; i < labelData.entries.Length; i++)
		{
			CountryLabelData.Entry entry = labelData.entries[i];
			if (string.IsNullOrEmpty(entry.displayName)) { continue; }
			if (entry.bakeFailed && !showFailedBakes) { continue; }
			if (entry.angularRadius <= 0f) { continue; }

			Label label = CreateLabel(entry, material, radius);
			if (label != null) { labels.Add(label); }
		}

		initialised = true;

		if (logInitTime)
		{
			Debug.Log($"[Country labels] created {labels.Count} labels in " +
				$"{timer.ElapsedMilliseconds} ms", this);
		}
	}

	/// <summary>
	/// Builds a material from the font atlas using TMP's overlay shader, which already
	/// has the depth state these labels need. The plain Distance Field shader declares
	/// ZTest [unity_GUIZTestMode] - a global set by Canvas rendering - and there is no
	/// Canvas in this scene, so its depth behaviour would be whatever happened to be left
	/// in that global.
	/// </summary>
	Material CreateOverlayMaterial()
	{
		Shader overlay = Shader.Find("TextMeshPro/Distance Field Overlay");
		if (overlay == null)
		{
			Debug.LogError($"{nameof(CountryLabelSystem)}: could not find the TMP overlay shader. " +
				"Assign labelMaterial explicitly, or add the shader to Always Included Shaders.", this);
			return null;
		}

		runtimeMaterial = new Material(fontAsset.material)
		{
			shader = overlay,
			hideFlags = HideFlags.HideAndDontSave
		};
		return runtimeMaterial;
	}

	Label CreateLabel(CountryLabelData.Entry entry, Material material, float radius)
	{
		var go = new GameObject(entry.displayName) { layer = layer };
		go.transform.SetParent(container, false);

		var text = go.AddComponent<TextMeshPro>();
		text.font = fontAsset;
		text.fontSharedMaterial = material;
		text.text = entry.displayName;
		text.color = textColour;
		text.alignment = TextAlignmentOptions.Center;
		text.overflowMode = TextOverflowModes.Overflow;

		// Size the rect to the text rather than setting a wrap mode, which keeps this
		// working across TMP versions that have renamed those properties.
		Vector2 preferred = text.GetPreferredValues();
		if (preferred.x <= 0.0001f) { Destroy(go); return null; }
		text.rectTransform.sizeDelta = preferred + Vector2.one * 0.5f;

		// Width the label should span on the ground: the country's inscribed circle.
		float targetWidth = 2f * entry.angularRadius * radius * fitFactor;
		float scale = Mathf.Clamp(targetWidth / preferred.x, minScale, maxScale);
		go.transform.localScale = Vector3.one * scale;

		Vector3 direction = GeoMaths.CoordinateToPoint(entry.anchor, 1f);

		var label = new Label
		{
			text = text,
			transform = go.transform,
			gameObject = go,
			anchorDirection = direction,
			worldPosition = direction * radius,
			worldHeight = preferred.y * scale,
			alpha = 0f,
			active = true
		};

		ApplyPose(label);
		SetLabelActive(label, false);
		return label;
	}

	void ApplyPose(Label label)
	{
		Vector3 up = label.anchorDirection;

		// Vector3.up is the north pole in this projection, so projecting it onto the
		// tangent plane gives local north. Same construction as TestbedCamera.LocalNorth.
		Vector3 north = Vector3.ProjectOnPlane(Vector3.up, up);
		if (north.sqrMagnitude < 1e-8f) { north = Vector3.ProjectOnPlane(Vector3.forward, up); }
		north.Normalize();

		// Forward is outward from the globe, so the readable face points at the sky.
		Vector3 facing = flipFacing ? -up : up;
		label.transform.SetPositionAndRotation(label.worldPosition,
			Quaternion.LookRotation(facing, north));
	}

	void LateUpdate()
	{
		if (!initialised || cam == null) { return; }

		Vector3 camPos = cam.transform.position;
		// Pixels per world unit at unit distance - the projected size of anything is then
		// just its world size times this over its distance. Avoids a WorldToScreenPoint
		// per label.
		float pixelsPerUnitAtUnitDistance =
			(Screen.height * 0.5f) / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);

		float fadeEnd = Mathf.Max(minPixelHeight * fadeBandFactor, minPixelHeight + 0.01f);
		int visible = 0;

		for (int i = 0; i < labels.Count; i++)
		{
			Label label = labels[i];

			Vector3 toCamera = camPos - label.worldPosition;
			float distance = toCamera.magnitude;
			if (distance < 1e-4f) { continue; }

			// Beyond the horizon, and foreshortened to illegibility well before that.
			float facing = Vector3.Dot(label.anchorDirection, toCamera / distance);
			float horizonFade = Mathf.InverseLerp(horizonFadeStart, horizonFadeEnd, facing);

			float pixelHeight = label.worldHeight * pixelsPerUnitAtUnitDistance / distance;
			float sizeFade = Mathf.InverseLerp(minPixelHeight, fadeEnd, pixelHeight);

			float alpha = horizonFade * sizeFade;

			// Hysteresis, so a label sitting exactly on a threshold doesn't flicker.
			if (label.active && alpha < 0.005f) { SetLabelActive(label, false); }
			else if (!label.active && alpha > 0.01f) { SetLabelActive(label, true); }

			if (!label.active) { continue; }

			visible++;
			// TMP_Text.alpha dirties the vertex data, so only write it when it moved.
			if (Mathf.Abs(alpha - label.alpha) > 0.002f)
			{
				label.alpha = alpha;
				label.text.alpha = alpha;
			}
		}

		VisibleCount = visible;
	}

	static void SetLabelActive(Label label, bool active)
	{
		label.active = active;
		label.gameObject.SetActive(active);
	}

	void OnDestroy()
	{
		if (runtimeMaterial != null)
		{
			if (Application.isPlaying) { Destroy(runtimeMaterial); }
			else { DestroyImmediate(runtimeMaterial); }
		}
	}
}
