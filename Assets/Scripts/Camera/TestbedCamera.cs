using System.Collections;
using UnityEngine;
using TerrainGeneration;

/// <summary>
/// Camera for the rendering testbed. Replaces the original GameCamera, which followed
/// the player aeroplane.
///
/// Two modes:
///   Orbit   - strategy-style view. Position is (longitude, latitude, altitude) on the
///             globe; orientation is (heading, pitch). This is the mode the thesis
///             measures in, and the mode SetView drives.
///   FreeFly - unconstrained debug camera for inspecting the atmosphere from arbitrary
///             angles, including from outside it.
///
/// Deliberately has no movement smoothing: every pose is a pure function of the state
/// fields, so a given view is bit-identical between runs. That matters more for
/// reproducible measurements than the extra polish would.
///
/// HARNESS NOTE: AtmosphereEffect rebuilds its sky and aerial-perspective LUTs during
/// Camera.onPreCull using the camera position (AtmosphereEffect.SetRaymarchParams). The
/// first frame after a large SetView jump therefore renders with LUTs built for the OLD
/// position. Always capture via SnapAndSettle(), never immediately after SetView.
/// </summary>
public class TestbedCamera : MonoBehaviour
{
	public enum Mode { Orbit, FreeFly }

	[Header("References")]
	public Camera cam;
	public TerrainHeightSettings heightSettings;

	[Header("Mode")]
	public Mode mode = Mode.Orbit;
	public KeyCode toggleModeKey = KeyCode.Tab;
	// Harness sets this false to take exclusive control of the pose.
	public bool inputEnabled = true;

	[Header("Orbit state")]
	public CoordinateDegrees coordinate = new CoordinateDegrees(0, 20);
	// Units above the terrain sphere (not above local ground height).
	public float altitude = 10f;
	// 0 = looking at the horizon, 90 = looking straight down.
	[Range(0f, 90f)] public float pitch = 55f;
	// Degrees clockwise from north.
	public float heading = 0f;

	[Header("Orbit tuning")]
	// Radians of surface arc per second, at referenceAltitude.
	public float panSpeed = 0.35f;
	public float referenceAltitude = 10f;
	public float headingSpeed = 60f;
	public float pitchSpeed = 40f;
	public float zoomSensitivity = 1.5f;
	public float minAltitude = 0.5f;
	// Keep inside the layer-6 (Earth) cull distance or the terrain vanishes while the
	// atmosphere keeps rendering. See RenderSettingsController.layerOverrides.
	public float maxAltitude = 250f;

	[Header("Free-fly tuning")]
	public float flySpeed = 20f;
	public float boostMultiplier = 5f;
	public float slowMultiplier = 0.2f;
	public bool scaleFlySpeedWithAltitude = true;
	public float mouseSensitivity = 2f;
	// Hold to look, rather than locking the cursor - keeps the Game view usable.
	public KeyCode lookModifier = KeyCode.Mouse1;
	// Keeps the horizon level, which makes atmospheric gradients much easier to judge.
	public bool gravityAlignedRoll = true;

	[Header("Optics")]
	public float fieldOfView = 60f;

	[Header("Home view")]
	// The view Reset returns to. Fly somewhere you like, then use the component's
	// "Set Home To Current View" context menu to capture it.
	public CameraView homeView = DefaultHomeView;
	public KeyCode resetKey = KeyCode.Backspace;

	[Header("Bookmarks")]
	// Key-bound saved views (Z X C V by default). Lives in an asset rather than on this
	// component so captures made during play mode survive exiting it.
	public CameraBookmarks bookmarks;
	// Hold this while pressing a bookmark key to overwrite that slot with the current
	// view. Set to None to capture on a bare press (not recommended - easy to clobber).
	public KeyCode captureModifier = KeyCode.LeftShift;

	const float defaultWorldRadius = 150f;
	bool warnedMissingHeightSettings;

	/// <summary>Radius of the terrain sphere. Altitude is measured from this.</summary>
	public float SurfaceRadius
	{
		get
		{
			if (heightSettings != null) { return heightSettings.worldRadius; }
			if (!warnedMissingHeightSettings)
			{
				warnedMissingHeightSettings = true;
				Debug.LogWarning($"{nameof(TestbedCamera)}: heightSettings not assigned, " +
					$"falling back to worldRadius = {defaultWorldRadius}.", this);
			}
			return defaultWorldRadius;
		}
	}

	public Mode CurrentMode => mode;

	void OnEnable()
	{
		if (cam == null) { cam = GetComponentInChildren<Camera>(); }
		ApplyOptics();
		ApplyPose();
	}

	void Update()
	{
		if (!inputEnabled) { return; }

		if (Input.GetKeyDown(resetKey))
		{
			ResetView();
			return;
		}

		if (Input.GetKeyDown(toggleModeKey))
		{
			SetMode(mode == Mode.Orbit ? Mode.FreeFly : Mode.Orbit);
		}

		if (HandleBookmarkInput()) { return; }

		if (mode == Mode.Orbit) { ReadOrbitInput(); }
		else { ReadFreeFlyInput(); }
	}

	// Pose is applied in LateUpdate so that Camera.onPreCull subscribers - the atmosphere
	// LUT dispatch and SimpleLodSystem - see a settled camera for the frame. Do not move
	// this to OnPreCull or FixedUpdate.
	void LateUpdate()
	{
		ApplyOptics();
		if (mode == Mode.Orbit) { ApplyPose(); }
	}

	// ----------------------------------------------------------------- orbit mode

	void ReadOrbitInput()
	{
		float dt = Time.unscaledDeltaTime;

		// Zoom first, so panning uses the altitude the user just selected.
		float scroll = Input.GetAxisRaw("Mouse ScrollWheel");
		if (!Mathf.Approximately(scroll, 0f))
		{
			altitude = Mathf.Clamp(altitude * Mathf.Exp(-scroll * zoomSensitivity), minAltitude, maxAltitude);
		}

		float strafe = AxisFromKeys(KeyCode.D, KeyCode.A) + AxisFromKeys(KeyCode.RightArrow, KeyCode.LeftArrow);
		float advance = AxisFromKeys(KeyCode.W, KeyCode.S) + AxisFromKeys(KeyCode.UpArrow, KeyCode.DownArrow);
		if (strafe != 0f || advance != 0f)
		{
			Pan(Mathf.Clamp(strafe, -1f, 1f), Mathf.Clamp(advance, -1f, 1f), dt);
		}

		heading += AxisFromKeys(KeyCode.E, KeyCode.Q) * headingSpeed * dt;
		heading = Mathf.Repeat(heading, 360f);

		pitch = Mathf.Clamp(pitch + AxisFromKeys(KeyCode.R, KeyCode.F) * pitchSpeed * dt, 0f, 90f);
	}

	/// <summary>
	/// Moves along a great circle rather than incrementing latitude/longitude directly.
	/// Naive lat/lon stepping crawls at the equator and spins near the poles; this keeps
	/// surface speed uniform. Arc scales with altitude so screen-space pan rate is roughly
	/// constant as you zoom.
	/// </summary>
	void Pan(float strafe, float advance, float dt)
	{
		GetOrbitFrame(out Vector3 up, out Vector3 flatFwd, out Vector3 right);

		Vector3 dir = flatFwd * advance + right * strafe;
		if (dir.sqrMagnitude < 1e-8f) { return; }
		dir.Normalize();

		float arc = panSpeed * (Mathf.Max(altitude, minAltitude) / Mathf.Max(referenceAltitude, 0.01f)) * dt;
		Vector3 newUp = (up * Mathf.Cos(arc) + dir * Mathf.Sin(arc)).normalized;
		coordinate = GeoMaths.PointToCoordinate(newUp).ConvertToDegrees();
	}

	/// <summary>
	/// Local tangent frame at the current coordinate. `up` is the surface normal,
	/// `flatFwd` is the heading direction projected onto the tangent plane, `right` is
	/// perpendicular to both.
	/// </summary>
	void GetOrbitFrame(out Vector3 up, out Vector3 flatFwd, out Vector3 right)
	{
		up = GeoMaths.CoordinateToPoint(coordinate.ConvertToRadians(), 1f);

		// Vector3.up is the north pole in this projection (CoordinateToPoint puts
		// latitude on +Y), so projecting it onto the tangent plane gives local north.
		Vector3 north = Vector3.ProjectOnPlane(Vector3.up, up);
		if (north.sqrMagnitude < 1e-8f)
		{
			// Exactly at a pole - any tangent direction is as good as another.
			north = Vector3.ProjectOnPlane(Vector3.forward, up);
		}
		north.Normalize();
		Vector3 east = Vector3.Cross(up, north);

		float h = heading * Mathf.Deg2Rad;
		flatFwd = north * Mathf.Cos(h) + east * Mathf.Sin(h);
		right = Vector3.Cross(up, flatFwd);
	}

	void ApplyPose()
	{
		GetOrbitFrame(out Vector3 up, out Vector3 flatFwd, out Vector3 right);

		Vector3 position = up * (SurfaceRadius + altitude);
		Vector3 fwd = Quaternion.AngleAxis(pitch, right) * flatFwd;

		// Cross(fwd, right) rather than `up` as the reference: stays exactly orthonormal
		// at pitch = 90, where fwd is antiparallel to up and LookRotation would degenerate.
		transform.SetPositionAndRotation(position, Quaternion.LookRotation(fwd, Vector3.Cross(fwd, right)));
	}

	// -------------------------------------------------------------- free-fly mode

	void ReadFreeFlyInput()
	{
		float dt = Time.unscaledDeltaTime;
		Vector3 planetUp = PlanetUp();

		if (Input.GetKey(lookModifier))
		{
			float mx = Input.GetAxisRaw("Mouse X") * mouseSensitivity;
			float my = Input.GetAxisRaw("Mouse Y") * mouseSensitivity;

			Vector3 fwd = Quaternion.AngleAxis(mx, planetUp) * transform.forward;

			Vector3 pitched = Quaternion.AngleAxis(-my, transform.right) * fwd;
			// Reject pitch that would put the view axis on the pole of the reference
			// frame, where LookRotation has no defined roll.
			if (Mathf.Abs(Vector3.Dot(pitched.normalized, planetUp)) < 0.999f) { fwd = pitched; }

			transform.rotation = gravityAlignedRoll
				? Quaternion.LookRotation(fwd, planetUp)
				: Quaternion.LookRotation(fwd, transform.up);
		}
		else if (gravityAlignedRoll)
		{
			// Keep the horizon level as the planet's up direction changes underneath us.
			Vector3 fwd = transform.forward;
			if (Mathf.Abs(Vector3.Dot(fwd, planetUp)) < 0.999f)
			{
				transform.rotation = Quaternion.LookRotation(fwd, planetUp);
			}
		}

		float speed = flySpeed;
		if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) { speed *= boostMultiplier; }
		if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) { speed *= slowMultiplier; }
		if (scaleFlySpeedWithAltitude)
		{
			float altitudeAboveSurface = Mathf.Max(transform.position.magnitude - SurfaceRadius, minAltitude);
			speed *= altitudeAboveSurface / Mathf.Max(referenceAltitude, 0.01f);
		}

		Vector3 move = transform.forward * (AxisFromKeys(KeyCode.W, KeyCode.S) + AxisFromKeys(KeyCode.UpArrow, KeyCode.DownArrow))
					 + transform.right * (AxisFromKeys(KeyCode.D, KeyCode.A) + AxisFromKeys(KeyCode.RightArrow, KeyCode.LeftArrow))
					 + planetUp * AxisFromKeys(KeyCode.Space, KeyCode.LeftControl);

		if (move.sqrMagnitude > 1e-8f)
		{
			transform.position += move.normalized * speed * dt;
		}
	}

	Vector3 PlanetUp()
	{
		Vector3 p = transform.position;
		return p.sqrMagnitude < 1e-8f ? Vector3.up : p.normalized;
	}

	// ------------------------------------------------------------- mode switching

	/// <summary>
	/// Switches mode without moving the camera. Entering Orbit reconstructs the orbit
	/// state from the current transform, so there is no jump-cut in either direction.
	/// </summary>
	public void SetMode(Mode newMode)
	{
		if (newMode == mode) { return; }

		if (newMode == Mode.Orbit) { SeedOrbitFromTransform(); }
		mode = newMode;
	}

	void SeedOrbitFromTransform()
	{
		Vector3 p = transform.position;
		float r = p.magnitude;
		if (r < 1e-4f) { return; }

		Vector3 up = p / r;
		coordinate = GeoMaths.PointToCoordinate(up).ConvertToDegrees();
		altitude = Mathf.Clamp(r - SurfaceRadius, minAltitude, maxAltitude);

		Vector3 fwd = transform.forward;
		Vector3 flatFwd = Vector3.ProjectOnPlane(fwd, up);
		if (flatFwd.sqrMagnitude < 1e-8f)
		{
			// Looking straight down: heading is undefined, so keep the existing value.
			pitch = 90f;
			return;
		}
		flatFwd.Normalize();

		pitch = Mathf.Clamp(Vector3.Angle(fwd, flatFwd), 0f, 90f);

		Vector3 north = Vector3.ProjectOnPlane(Vector3.up, up);
		if (north.sqrMagnitude < 1e-8f) { north = Vector3.ProjectOnPlane(Vector3.forward, up); }
		north.Normalize();
		heading = Mathf.Repeat(Vector3.SignedAngle(north, flatFwd, up), 360f);
	}

	// -------------------------------------------------------------- harness API

	[System.Serializable]
	public struct CameraView
	{
		public CoordinateDegrees coordinate;
		public float altitude;
		public float pitch;
		public float heading;
		public float fieldOfView;
	}

	static CameraView DefaultHomeView => new CameraView
	{
		coordinate = new CoordinateDegrees(0f, 20f),
		altitude = 10f,
		pitch = 55f,
		heading = 0f,
		fieldOfView = 60f
	};

	/// <summary>
	/// Snaps back to <see cref="homeView"/>, switching to Orbit mode. Bound to
	/// <see cref="resetKey"/>, and the way to recover from getting lost in free-fly.
	/// </summary>
	public void ResetView()
	{
		SetView(homeView);
	}

	/// <summary>
	/// Bookmark keys: press to jump, hold <see cref="captureModifier"/> to overwrite.
	/// Returns true if a key was consumed, so movement input is skipped that frame.
	/// </summary>
	bool HandleBookmarkInput()
	{
		if (bookmarks == null) { return false; }

		for (int i = 0; i < bookmarks.Count; i++)
		{
			KeyCode key = bookmarks.KeyAt(i);
			if (key == KeyCode.None || !Input.GetKeyDown(key)) { continue; }

			bool capturing = captureModifier == KeyCode.None || Input.GetKey(captureModifier);
			if (capturing)
			{
				bookmarks.Capture(i, GetView());
				Debug.Log($"Saved camera bookmark '{bookmarks.LabelAt(i)}' ({key}): " +
					$"{coordinate.latitude:0.00}, {coordinate.longitude:0.00} " +
					$"alt {altitude:0.0} pitch {pitch:0.0} heading {heading:0.0}", this);
			}
			else if (bookmarks.TryGetView(i, out CameraView view))
			{
				SetView(view);
			}
			else
			{
				// Empty slot. Say so rather than silently doing nothing - otherwise it
				// looks like the binding is broken.
				Debug.Log($"Camera bookmark '{bookmarks.LabelAt(i)}' ({key}) is empty. " +
					$"Hold {captureModifier} and press {key} to save the current view.", this);
			}
			return true;
		}
		return false;
	}

	public bool JumpToBookmark(int index)
	{
		if (bookmarks == null || !bookmarks.TryGetView(index, out CameraView view)) { return false; }

		SetView(view);
		return true;
	}

	public void CaptureBookmark(int index)
	{
		if (bookmarks != null) { bookmarks.Capture(index, GetView()); }
	}

	public CameraView GetView()
	{
		return new CameraView
		{
			coordinate = coordinate,
			altitude = altitude,
			pitch = pitch,
			heading = heading,
			fieldOfView = fieldOfView
		};
	}

	/// <summary>
	/// Snaps to an exact view. Forces Orbit mode and applies the transform immediately
	/// rather than waiting for LateUpdate, so a caller can position and then act within
	/// the same frame.
	///
	/// Does NOT guarantee the next rendered frame is correct - the atmosphere LUTs lag by
	/// a frame. Use SnapAndSettle for capture.
	/// </summary>
	public void SetView(CameraView view)
	{
		SetView(view.coordinate, view.altitude, view.pitch, view.heading, view.fieldOfView);
	}

	public void SetView(CoordinateDegrees coord, float altitude, float pitch, float heading, float fov = -1f)
	{
		mode = Mode.Orbit;
		coordinate = coord;
		this.altitude = Mathf.Clamp(altitude, minAltitude, maxAltitude);
		this.pitch = Mathf.Clamp(pitch, 0f, 90f);
		this.heading = Mathf.Repeat(heading, 360f);
		if (fov > 0f) { fieldOfView = fov; }

		ApplyOptics();
		ApplyPose();
	}

	/// <summary>
	/// Sets a view and waits for the render to catch up. Use this before any screenshot or
	/// timing capture.
	///
	/// The wait is not cosmetic: the atmosphere's sky and aerial-perspective LUTs are
	/// rebuilt from the camera position during onPreCull, so the frame immediately after a
	/// jump is rendered with LUTs belonging to the previous position. Two frames covers
	/// the dispatch and its use.
	/// </summary>
	public IEnumerator SnapAndSettle(CameraView view, int framesToSettle = 2)
	{
		SetView(view);
		for (int i = 0; i < framesToSettle; i++)
		{
			yield return null;
		}
	}

	void ApplyOptics()
	{
		if (cam != null && !Mathf.Approximately(cam.fieldOfView, fieldOfView))
		{
			cam.fieldOfView = fieldOfView;
		}
	}

	// ------------------------------------------------------------------- helpers

	static float AxisFromKeys(KeyCode positive, KeyCode negative)
	{
		float v = 0f;
		if (Input.GetKey(positive)) { v += 1f; }
		if (Input.GetKey(negative)) { v -= 1f; }
		return v;
	}

	[ContextMenu("Apply Orbit View")]
	void ApplyViewFromInspector()
	{
		mode = Mode.Orbit;
		ApplyOptics();
		ApplyPose();
	}

	[ContextMenu("Reset To Home View")]
	void ResetViewFromInspector()
	{
		ResetView();
	}

	[ContextMenu("Set Home To Current View")]
	void SetHomeToCurrentView()
	{
		homeView = GetView();
	}

	// Called when the component is first added, and on Inspector > Reset.
	void Reset()
	{
		homeView = DefaultHomeView;
	}

	void OnValidate()
	{
		minAltitude = Mathf.Max(minAltitude, 0.01f);
		maxAltitude = Mathf.Max(maxAltitude, minAltitude);
		altitude = Mathf.Clamp(altitude, minAltitude, maxAltitude);
		fieldOfView = Mathf.Clamp(fieldOfView, 1f, 179f);

		// A home view serialized before this field existed deserializes to all-zero,
		// which would reset the camera to the planet surface. Treat that as unset.
		if (homeView.altitude <= 0f)
		{
			homeView = DefaultHomeView;
		}
	}
}
