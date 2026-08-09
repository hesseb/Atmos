using UnityEngine;

namespace SolarSystem
{
	/// <summary>
	/// Solves for the time of day that puts the sun at a given elevation above an
	/// observer. Pure maths, no scene state - safe to call from the measurement harness.
	///
	/// Assumes the solar system is running in GEOCENTRIC mode (the mode the scene uses),
	/// where the Earth is fixed at the origin with identity rotation and the sun is moved
	/// instead. In heliocentric mode the Earth transform rotates under a stationary
	/// camera, so an observer's latitude/longitude is not fixed and these results do not
	/// apply.
	///
	/// The key fact that makes this exact rather than a search: in geocentric mode
	///
	///     dirToSun = Ry(360*(dayT + yearT)) * Rz(tilt) * (-earthPosNormalised)
	///
	/// so as dayT advances the sun direction simply rotates about the world Y axis. The
	/// dot product with any fixed observer up-vector is therefore a pure sinusoid in dayT:
	///
	///     sin(elevation) = offset + amplitude * cos(360*(dayT + yearT) - phase)
	///
	/// which inverts directly. Verified against a brute-force evaluation of
	/// EarthOrbit.UpdateOrbit + Sun.UpdateOrbit to within 3e-15.
	/// </summary>
	public static class SolarTime
	{
		/// <summary>
		/// The sun-elevation curve over one day, for a fixed observer and time of year.
		/// sin(elevation) = offset + amplitude * cos(theta - phase), theta in degrees.
		/// </summary>
		public struct DayCurve
		{
			public float offset;
			public float amplitude;
			public float phaseDegrees;

			/// <summary>True at a pole, where elevation does not vary over the day at all.</summary>
			public bool IsDegenerate => amplitude < 1e-6f;

			public float MaxSinElevation => offset + amplitude;
			public float MinSinElevation => offset - amplitude;
		}

		/// <summary>Unit vector from the origin toward the sun, for a given time.</summary>
		public static Vector3 DirectionToSun(EarthOrbit earth, float dayT, float yearT)
		{
			return (Quaternion.Euler(0f, 360f * (dayT + yearT), 0f) * TiltedOrbitVector(earth, yearT)).normalized;
		}

		/// <summary>Sun elevation above the observer's horizon, in degrees.</summary>
		public static float ElevationDegrees(EarthOrbit earth, Vector3 observerUp, float dayT, float yearT)
		{
			float s = Vector3.Dot(observerUp.normalized, DirectionToSun(earth, dayT, yearT));
			return Mathf.Asin(Mathf.Clamp(s, -1f, 1f)) * Mathf.Rad2Deg;
		}

		public static DayCurve GetDayCurve(EarthOrbit earth, Vector3 observerUp, float yearT)
		{
			Vector3 up = observerUp.normalized;
			Vector3 v = TiltedOrbitVector(earth, yearT);

			// dot(up, Ry(theta) * v) expanded into A + P*cos(theta) + Q*sin(theta).
			float p = up.x * v.x + up.z * v.z;
			float q = up.x * v.z - up.z * v.x;

			return new DayCurve
			{
				offset = up.y * v.y,
				amplitude = Mathf.Sqrt(p * p + q * q),
				phaseDegrees = Mathf.Atan2(q, p) * Mathf.Rad2Deg
			};
		}

		/// <summary>
		/// Finds the dayT at which the sun sits at <paramref name="elevationDegrees"/>
		/// above the observer's horizon, either while rising or while setting.
		///
		/// Returns false when the elevation is unreachable at this latitude and time of
		/// year (polar day or polar night), or when the observer is at a pole, where
		/// elevation does not change over the day at all. In both cases the caller should
		/// leave the time alone or adjust yearT instead.
		/// </summary>
		public static bool TrySolveDayT(EarthOrbit earth, Vector3 observerUp, float yearT,
			float elevationDegrees, bool rising, out float dayT)
		{
			dayT = 0f;
			DayCurve curve = GetDayCurve(earth, observerUp, yearT);
			if (curve.IsDegenerate) { return false; }

			float target = Mathf.Sin(elevationDegrees * Mathf.Deg2Rad);
			float cos = (target - curve.offset) / curve.amplitude;
			if (Mathf.Abs(cos) > 1f) { return false; }

			// Two solutions per day. The one before the phase peak is rising, after is setting.
			float delta = Mathf.Acos(cos) * Mathf.Rad2Deg;
			float theta = curve.phaseDegrees + (rising ? -delta : delta);

			dayT = Mathf.Repeat(theta / 360f - yearT, 1f);
			return true;
		}

		/// <summary>dayT of local solar noon (highest sun) or midnight (lowest).</summary>
		public static float SolveDayTForExtreme(EarthOrbit earth, Vector3 observerUp, float yearT, bool highest)
		{
			DayCurve curve = GetDayCurve(earth, observerUp, yearT);
			float theta = curve.phaseDegrees + (highest ? 0f : 180f);
			return Mathf.Repeat(theta / 360f - yearT, 1f);
		}

		/// <summary>
		/// Local solar time in hours [0, 24), where 12 is the moment the sun is highest.
		/// This is solar time at the observer's longitude, not a clock time - there are no
		/// time zones here.
		/// </summary>
		public static float LocalSolarHours(EarthOrbit earth, Vector3 observerUp, float dayT, float yearT)
		{
			DayCurve curve = GetDayCurve(earth, observerUp, yearT);
			if (curve.IsDegenerate) { return 12f; }

			float theta = 360f * (dayT + yearT);
			float hourAngle = Mathf.DeltaAngle(curve.phaseDegrees, theta); // 0 at solar noon
			return Mathf.Repeat(12f + hourAngle / 15f, 24f);               // 15 degrees per hour
		}

		public static string FormatHours(float hours)
		{
			int h = Mathf.FloorToInt(hours);
			int m = Mathf.FloorToInt((hours - h) * 60f);
			if (m >= 60) { m -= 60; h += 1; }
			return $"{h % 24:00}:{m:00}";
		}

		// -earthPos, rotated by the axial tilt. Fixed for a given time of year; the day
		// cycle is just this vector spun about Y.
		static Vector3 TiltedOrbitVector(EarthOrbit earth, float yearT)
		{
			Vector2 e = Orbit.CalculatePointOnOrbit(earth.periapis, earth.apoapsis, yearT);
			Vector3 toSun = -new Vector3(e.x, 0f, e.y).normalized;
			return Quaternion.Euler(0f, 0f, earth.tilt) * toSun;
		}
	}
}
