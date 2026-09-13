using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SolarSystem
{
	[ExecuteInEditMode]
	public class SolarSystemManager : MonoBehaviour
	{
		public bool animate;

		[Header( "Durations" )]
		// Allow flexible day/month/year durations since real timescales are a bit slow...
		public float dayDurationMinutes;
		public float monthDurationMinutes;
		public float yearDurationMinutes;
		public float timeMultiplier = 1f;

		[Header( "References" )]
		public Sun sun;
		public EarthOrbit earth;
		public Moon moon;
		public StarRenderer stars;

		[Header( "Time state" )]
		[Range( 0, 1 )]
		public float dayT;
		[Range( 0, 1 )]
		public float monthT;
		[Range( 0, 1 )]
		public float yearT;

		[Header( "Debug" )]
		public bool geocentric;


		void Update()
		{
			if ( animate && Application.isPlaying )
			{
				float step = timeMultiplier * Time.deltaTime;
				dayT += 1 / ( dayDurationMinutes * 60 ) * step;
				monthT += 1 / ( monthDurationMinutes * 60 ) * step;
				yearT += 1 / ( yearDurationMinutes * 60 ) * step;

				// Repeat rather than %, so a negative timeMultiplier wraps correctly
				dayT = Mathf.Repeat( dayT, 1 );
				monthT = Mathf.Repeat( monthT, 1 );
				yearT = Mathf.Repeat( yearT, 1 );
			}

			earth?.UpdateOrbit( yearT, dayT, geocentric );
			sun?.UpdateOrbit( earth, geocentric );
			moon?.UpdateOrbit( monthT, earth, geocentric );
			stars?.UpdateFixedStars( earth, geocentric );

		}

		public void SetTimes( float dayT, float monthT, float yearT )
		{
			this.dayT = dayT;
			this.monthT = monthT;
			this.yearT = yearT;
		}
	}
}
