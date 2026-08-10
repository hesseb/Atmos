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
	// Optional. Supplies which country is hovered, so its label can be emphasised.
	public GlobePicker picker;
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

	[Header("Outline")]
	// A dark outline is what makes labels legible over pale desert and bright ice alike -
	// the same contrast problem the border highlight has.
	public Color outlineColour = new Color(0.03f, 0.02f, 0f, 1f);
	// TMP scales this by the material's _ScaleRatioA. Much above ~0.4 the outline exceeds
	// the atlas padding and starts to clip.
	[Range(0f, 0.6f)] public float outlineWidth = 0.2f;
	[Range(0f, 1f)] public float outlineSoftness = 0.1f;
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

	[Header("Hover emphasis")]
	// Idle labels sit back so the map reads first; the hovered one comes fully forward.
	[Range(0f, 1f)] public float idleBrightness = 0.8f;
	[Range(0f, 1f)] public float idleAlpha = 0.65f;
	public Color hoverColour = Color.white;
	public float hoverScaleMultiplier = 1.3f;
	public float hoverTransitionDuration = 0.12f;
	// Show the hovered country's name even when it is too small to pass the size filter.
	public bool hoverOverridesSizeFilter = true;

	[Header("Debug")]
	public bool logInitTime = true;
	// TMP's generated text reads correctly from its local -Z, so the object's forward has
	// to point into the globe for the face to be legible from outside. Determined by
	// looking at it rather than by reasoning about TMP's winding.
	public bool flipFacing = true;

	class Label
	{
		public TextMeshPro text;
		public Transform transform;
		public GameObject gameObject;
		public int countryIndex;
		public Vector3 anchorDirection;
		public Vector3 worldPosition;
		public float baseScale;
		public float worldHeight;   // of the text, in world units
		public float hover;         // 0 idle, 1 fully emphasised
		public Color appliedColour;
		public float appliedScale;
		public bool active;
	}

	readonly List<Label> labels = new List<Label>();
	Material runtimeMaterial;
	Transform container;
	bool initialised;

	float GlobeRadius => heightSettings != null ? heightSettings.worldRadius : 150f;

	public int VisibleCount { get; private set; }

	/// <summary>
	/// Re-places every label for a new globe radius.
	///
	/// Labels bake `worldPosition = anchorDirection * radius` when they are built, once, in
	/// Start - so a planet scale applied afterwards left them orbiting the radius the globe
	/// used to have, hanging in space above the surface. The unit anchor direction is kept, so
	/// re-placing them is just a multiply.
	/// </summary>
	public void SetGlobeRadius(float radius)
	{
		if (labels == null) { return; }

		foreach (Label label in labels)
		{
			if (label == null) { continue; }
			label.worldPosition = label.anchorDirection * radius;
		}
	}

	void Start()
	{
		Initialise();
	}

	void Initialise()
	{
		if (initialised) { return; }

		if (cam == null) { cam = Camera.main; }
		if (picker == null) { picker = GetComponent<GlobePicker>(); }
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

		Material material = CreateMaterial();
		if (material == null) { return; }

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
			if (label != null)
			{
				label.countryIndex = i;
				labels.Add(label);
			}
		}

		initialised = true;

		if (logInitTime)
		{
			Debug.Log($"[Country labels] created {labels.Count} labels in " +
				$"{timer.ElapsedMilliseconds} ms", this);
		}
	}

	/// <summary>
	/// Builds the shared label material, always as a runtime instance.
	///
	/// Even when labelMaterial is supplied it is copied rather than used directly, because
	/// the outline settings are written onto it - writing those to an assigned asset would
	/// modify that asset on disk.
	///
	/// Without one, it derives from the font atlas using TMP's overlay shader, which
	/// already has the depth state these labels need. The plain Distance Field shader
	/// declares ZTest [unity_GUIZTestMode], a global set by Canvas rendering, and there is
	/// no Canvas in this scene - its depth behaviour would be whatever was left in that
	/// global.
	/// </summary>
	Material CreateMaterial()
	{
		if (labelMaterial != null)
		{
			runtimeMaterial = new Material(labelMaterial) { hideFlags = HideFlags.HideAndDontSave };
		}
		else
		{
			Shader overlay = Shader.Find("TextMeshPro/Distance Field Overlay");
			if (overlay == null)
			{
				Debug.LogError($"{nameof(CountryLabelSystem)}: could not find the TMP overlay " +
					"shader. Assign labelMaterial explicitly, or add the shader to Always " +
					"Included Shaders.", this);
				return null;
			}

			runtimeMaterial = new Material(fontAsset.material)
			{
				shader = overlay,
				hideFlags = HideFlags.HideAndDontSave
			};
		}

		ApplyOutline(runtimeMaterial);
		return runtimeMaterial;
	}

	/// <summary>
	/// The TMP SDF shaders have no OUTLINE keyword - the outline is always compiled in and
	/// driven entirely by _OutlineWidth, which the shader scales by the material's
	/// _ScaleRatioA. That ratio is already set correctly on the font asset's material, so
	/// these three properties are all that is needed.
	/// </summary>
	// Names taken from TMP_SDF Overlay.shader directly, rather than TMPro.ShaderUtilities,
	// so this does not depend on which TMP version the package resolves to.
	static readonly int OutlineColourId = Shader.PropertyToID("_OutlineColor");
	static readonly int OutlineWidthId = Shader.PropertyToID("_OutlineWidth");
	static readonly int OutlineSoftnessId = Shader.PropertyToID("_OutlineSoftness");

	void ApplyOutline(Material material)
	{
		material.SetColor(OutlineColourId, outlineColour);
		material.SetFloat(OutlineWidthId, outlineWidth);
		material.SetFloat(OutlineSoftnessId, outlineSoftness);
	}

	/// <summary>Re-applies outline settings to the live material, for tuning in play mode.</summary>
	void OnValidate()
	{
		if (Application.isPlaying && runtimeMaterial != null) { ApplyOutline(runtimeMaterial); }
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
			baseScale = scale,
			appliedScale = scale,
			worldHeight = preferred.y * scale,
			appliedColour = new Color(0f, 0f, 0f, -1f), // forces the first write
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
		int hovered = picker != null ? picker.HoveredCountryIndex : -1;
		float hoverStep = hoverTransitionDuration > 0f
			? Time.unscaledDeltaTime / hoverTransitionDuration
			: 1f;
		Color idleColour = textColour * idleBrightness;
		int visible = 0;

		for (int i = 0; i < labels.Count; i++)
		{
			Label label = labels[i];

			float hoverTarget = label.countryIndex == hovered ? 1f : 0f;
			if (!Mathf.Approximately(label.hover, hoverTarget))
			{
				label.hover = Mathf.MoveTowards(label.hover, hoverTarget, hoverStep);
			}

			Vector3 toCamera = camPos - label.worldPosition;
			float distance = toCamera.magnitude;
			if (distance < 1e-4f) { continue; }

			// Beyond the horizon, and foreshortened to illegibility well before that.
			float facing = Vector3.Dot(label.anchorDirection, toCamera / distance);
			float horizonFade = Mathf.InverseLerp(horizonFadeStart, horizonFadeEnd, facing);

			float pixelHeight = label.worldHeight * pixelsPerUnitAtUnitDistance / distance;
			float sizeFade = Mathf.InverseLerp(minPixelHeight, fadeEnd, pixelHeight);
			// A hovered country's name is worth showing even if it is below the size
			// threshold - the cursor is already saying which country is meant.
			if (hoverOverridesSizeFilter) { sizeFade = Mathf.Max(sizeFade, label.hover); }

			// Idle labels sit back; the hovered one comes to full opacity.
			float alpha = horizonFade * sizeFade * Mathf.Lerp(idleAlpha, 1f, label.hover);

			// Hysteresis, so a label sitting exactly on a threshold doesn't flicker.
			if (label.active && alpha < 0.005f) { SetLabelActive(label, false); }
			else if (!label.active && alpha > 0.01f) { SetLabelActive(label, true); }

			if (!label.active) { continue; }

			visible++;

			Color target = Color.Lerp(idleColour, hoverColour, label.hover);
			target.a = alpha;
			// TMP_Text.color dirties the vertex colours, so only write it when it moved.
			if (ColourDiffers(target, label.appliedColour))
			{
				label.appliedColour = target;
				label.text.color = target;
			}

			float scale = label.baseScale * Mathf.Lerp(1f, hoverScaleMultiplier, label.hover);
			if (!Mathf.Approximately(scale, label.appliedScale))
			{
				label.appliedScale = scale;
				label.transform.localScale = Vector3.one * scale;
			}
		}

		VisibleCount = visible;
	}

	static bool ColourDiffers(Color a, Color b)
	{
		return Mathf.Abs(a.r - b.r) > 0.002f || Mathf.Abs(a.g - b.g) > 0.002f
			|| Mathf.Abs(a.b - b.b) > 0.002f || Mathf.Abs(a.a - b.a) > 0.002f;
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
