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
	// -90 = straight up at the sky, 0 = horizon, +90 = straight down.
	[Range(-90f, 90f)] public float pitch = 55f;
	// Degrees clockwise from north.
	public float heading = 0f;
	// Rotation about the view axis. Stays 0 under normal use, but free-fly with
	// gravityAlignedRoll off can produce it, and bookmarks preserve it.
	[Range(-180f, 180f)] public float roll = 0f;

	[Header("Orbit tuning")]
	// Radians of surface arc per second, at referenceAltitude.
	public float panSpeed = 0.35f;
	public float referenceAltitude = 10f;

	[Tooltip("Minimum altitude to come out of a camera mode switch at. Only ever raises, so a " +
		"camera already further out keeps its height. Scaled with the planet by the world scale " +
		"controller, since terrain grows with it and the field of view does not.")]
	public float modeSwitchAltitude = 40f;
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
	// Enter play mode at the home view rather than wherever the scene happened to be
	// saved, so the demo always opens on the same shot.
	public bool startAtHomeView = true;

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

	void Start()
	{
		// Deliberately in Start, not OnEnable. LoadingManager deactivates and re-activates
		// the Game root during the world bootstrap, so OnEnable can fire more than once,
		// and a later re-enable should not yank the camera back to home. Start runs before
		// the first frame is drawn, so there is no flash of the serialized position.
		if (startAtHomeView) { ResetView(); }
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

		// Full range: F tilts up past the horizon to the zenith, R down to nadir.
		pitch = Mathf.Clamp(pitch + AxisFromKeys(KeyCode.R, KeyCode.F) * pitchSpeed * dt, -90f, 90f);
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
		GetTangentFrame(up, heading, out flatFwd, out right);
	}

	/// <summary>
	/// Vector3.up is the north pole in this projection (CoordinateToPoint puts latitude
	/// on +Y), so projecting it onto the tangent plane gives local north.
	/// </summary>
	static Vector3 LocalNorth(Vector3 up)
	{
		Vector3 north = Vector3.ProjectOnPlane(Vector3.up, up);
		if (north.sqrMagnitude < 1e-8f)
		{
			// Exactly at a pole - any tangent direction is as good as another.
			north = Vector3.ProjectOnPlane(Vector3.forward, up);
		}
		return north.normalized;
	}

	static void GetTangentFrame(Vector3 up, float headingDegrees, out Vector3 flatFwd, out Vector3 right)
	{
		Vector3 north = LocalNorth(up);
		Vector3 east = Vector3.Cross(up, north);

		float h = headingDegrees * Mathf.Deg2Rad;
		flatFwd = north * Mathf.Cos(h) + east * Mathf.Sin(h);
		right = Vector3.Cross(up, flatFwd);
	}

	/// <summary>
	/// World pose for a view, without needing a camera instance.
	///
	/// Public and static so the benchmark harness can predict, off the clock, exactly the
	/// pose a planned frame will produce - the plan and the camera must not be able to
	/// disagree, so they share this one implementation.
	/// </summary>
	public static void ComputePose(CameraView view, float surfaceRadius,
		out Vector3 position, out Quaternion rotation)
	{
		Vector3 up = GeoMaths.CoordinateToPoint(view.coordinate.ConvertToRadians(), 1f);
		GetTangentFrame(up, view.heading, out Vector3 flatFwd, out Vector3 right);

		position = up * (surfaceRadius + view.altitude);
		Vector3 fwd = Quaternion.AngleAxis(view.pitch, right) * flatFwd;

		// Cross(fwd, right) rather than `up` as the reference: stays exactly orthonormal
		// at pitch = +/-90, where fwd is parallel to up and LookRotation would degenerate.
		rotation = Quaternion.LookRotation(fwd, Vector3.Cross(fwd, right));
		if (Mathf.Abs(view.roll) > 1e-4f)
		{
			rotation = Quaternion.AngleAxis(view.roll, fwd) * rotation;
		}
	}

	/// <summary>
	/// Places the camera a given height above the surface, in whichever mode it is in.
	///
	/// `altitude` alone is not enough. It only reaches the transform through ApplyPose, which
	/// runs in Orbit mode only - in free-fly the position is authoritative and altitude is merely
	/// derived from it. So changing the planet's radius under a free-flying camera left it at its
	/// old world radius, which on a larger planet is underground.
	///
	/// Direction is preserved, so the swap reads as a zoom rather than a jump to somewhere else.
	/// </summary>
	public void SetAltitudeAboveSurface(float targetAltitude)
	{
		this.altitude = Mathf.Clamp(targetAltitude, minAltitude, maxAltitude);

		if (mode == Mode.Orbit)
		{
			ApplyPose();
			return;
		}

		// Free-fly: move along the current outward direction to the new radius, keeping the
		// rotation. Falling back to the pole only matters if the camera is exactly at the centre.
		Vector3 outward = transform.position.sqrMagnitude > 1e-6f
			? transform.position.normalized
			: Vector3.up;
		transform.position = outward * (SurfaceRadius + this.altitude);
	}

	void ApplyPose()
	{
		var view = new CameraView
		{
			coordinate = coordinate,
			altitude = altitude,
			pitch = pitch,
			heading = heading,
			roll = roll,
			fieldOfView = fieldOfView
		};

		ComputePose(view, SurfaceRadius, out Vector3 position, out Quaternion rotation);
		transform.SetPositionAndRotation(position, rotation);
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

		// Come out of the switch at a height that can be flown from.
		//
		// Free-fly does not maintain `altitude` - the transform is authoritative there and the
		// field is only derived on the way back, through a Clamp to minAltitude. So a camera
		// that ended up at or under the surface reappears pinned to it at the closest possible
		// zoom, which means re-orienting by hand after every toggle.
		//
		// A dedicated value rather than referenceAltitude, which is a *speed* reference and is
		// too low to orient from: at 10 units a 60 degree field of view spans only 11.5 units
		// of ground, and terrain features grow with the planet while the field of view does
		// not - so at x16 that is a twentieth of what the same altitude shows at x1.
		float comfortable = Mathf.Clamp(modeSwitchAltitude, minAltitude, maxAltitude);
		float current = transform.position.magnitude - SurfaceRadius;
		if (current < comfortable) { SetAltitudeAboveSurface(comfortable); }

		// Temporary, to find out why this appears to have no effect.
		Debug.Log($"[TestbedCamera] -> {mode}: surfaceRadius {SurfaceRadius:F1}, was {current:F1}, " +
			$"modeSwitchAltitude {modeSwitchAltitude:F1}, clamped to {comfortable:F1} " +
			$"(min {minAltitude:F1}, max {maxAltitude:F1}), now altitude {altitude:F1} " +
			$"at radius {transform.position.magnitude:F1}", this);
	}

	/// <summary>
	/// Derives the full orbit parameterisation from the camera's actual transform.
	///
	/// (longitude, latitude, altitude, heading, pitch, roll) is a complete description of
	/// any camera pose, so this round-trips exactly with <see cref="ApplyPose"/> - verified
	/// numerically over 300+ orientations including straight up, straight down, full roll
	/// and near-pole latitudes. That is what lets a bookmark captured in free-fly be
	/// restored exactly in orbit mode.
	/// </summary>
	CameraView ViewFromTransform()
	{
		CameraView view = new CameraView
		{
			coordinate = coordinate,
			altitude = altitude,
			pitch = pitch,
			heading = heading,
			roll = roll,
			fieldOfView = fieldOfView
		};

		Vector3 p = transform.position;
		float r = p.magnitude;
		if (r < 1e-4f) { return view; }

		Vector3 up = p / r;
		view.coordinate = GeoMaths.PointToCoordinate(up).ConvertToDegrees();
		view.altitude = Mathf.Clamp(r - SurfaceRadius, minAltitude, maxAltitude);

		Vector3 fwd = transform.forward;
		view.pitch = Mathf.Asin(Mathf.Clamp(-Vector3.Dot(fwd, up), -1f, 1f)) * Mathf.Rad2Deg;

		Vector3 flat = Vector3.ProjectOnPlane(fwd, up);
		if (flat.sqrMagnitude < 1e-10f)
		{
			// Looking straight up or down: the view axis carries no heading, so take it
			// from the camera's own up vector, which at +/-90 pitch lies along the
			// heading direction (negated when looking up).
			Vector3 camFlat = Vector3.ProjectOnPlane(transform.up, up);
			if (camFlat.sqrMagnitude < 1e-10f) { return view; }
			flat = view.pitch > 0f ? camFlat : -camFlat;
		}
		flat.Normalize();
		view.heading = Mathf.Repeat(Vector3.SignedAngle(LocalNorth(up), flat, up), 360f);

		// Roll is whatever is left between the zero-roll reference up for this
		// heading/pitch and the camera's actual up.
		GetTangentFrame(up, view.heading, out Vector3 flatFwd, out Vector3 right);
		Vector3 refFwd = Quaternion.AngleAxis(view.pitch, right) * flatFwd;
		view.roll = Vector3.SignedAngle(Vector3.Cross(refFwd, right), transform.up, fwd);

		return view;
	}

	void SeedOrbitFromTransform()
	{
		CameraView view = ViewFromTransform();
		coordinate = view.coordinate;
		altitude = view.altitude;
		pitch = view.pitch;
		heading = view.heading;
		roll = view.roll;
	}

	// -------------------------------------------------------------- harness API

	/// <summary>
	/// A complete camera pose. These six values determine the transform exactly, so a
	/// view captured in either mode restores identically.
	/// </summary>
	[System.Serializable]
	public struct CameraView
	{
		public CoordinateDegrees coordinate;
		public float altitude;
		public float pitch;
		public float heading;
		public float roll;
		public float fieldOfView;
	}

	static CameraView DefaultHomeView => new CameraView
	{
		coordinate = new CoordinateDegrees(0f, 20f),
		altitude = 10f,
		pitch = 55f,
		heading = 0f,
		roll = 0f,
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
				// Read through GetView, not the fields - in free-fly the fields are stale.
				CameraView captured = GetView();
				bookmarks.Capture(i, captured);
				Debug.Log($"Saved camera bookmark '{bookmarks.LabelAt(i)}' ({key}): " +
					$"lat {captured.coordinate.latitude:0.00} lon {captured.coordinate.longitude:0.00} " +
					$"alt {captured.altitude:0.0} heading {captured.heading:0.0} " +
					$"pitch {captured.pitch:0.0} roll {captured.roll:0.0} fov {captured.fieldOfView:0.0}", this);
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

	/// <summary>
	/// The camera's current pose. In free-fly the orbit fields are stale - free-fly moves
	/// the transform directly and never writes back - so the transform is read instead.
	/// Without this, capturing a bookmark while free-flying would store wherever you were
	/// before entering free-fly.
	/// </summary>
	public CameraView GetView()
	{
		if (mode == Mode.FreeFly) { return ViewFromTransform(); }

		return new CameraView
		{
			coordinate = coordinate,
			altitude = altitude,
			pitch = pitch,
			heading = heading,
			roll = roll,
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
		SetView(view.coordinate, view.altitude, view.pitch, view.heading, view.roll, view.fieldOfView);
	}

	public void SetView(CoordinateDegrees coord, float altitude, float pitch, float heading,
		float roll = 0f, float fov = -1f)
	{
		mode = Mode.Orbit;
		coordinate = coord;
		this.altitude = Mathf.Clamp(altitude, minAltitude, maxAltitude);
		this.pitch = Mathf.Clamp(pitch, -90f, 90f);
		this.heading = Mathf.Repeat(heading, 360f);
		this.roll = Mathf.DeltaAngle(0f, roll);
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
