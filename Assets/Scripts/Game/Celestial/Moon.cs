using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SolarSystem
{
	public class Moon : MonoBehaviour
	{
		public float moonOrbitAngle;
		public float moonTilt;
		public float periapsis;
		public float apoapsis;
		public float dstMultiplier;
		public float size;
		public int resolution;

		public Renderer moonRenderer;

		[Header( "Lighting" )]
		[Tooltip( "Colour of moonlight for shaders that sample it." )]
		public Color moonLightColour = new Color( 0.85f, 0.89f, 1f );
		[Tooltip( "Scales moonLightColour." )]
		public float moonLightIntensity = 0.06f;
		[Tooltip( "For the moon's phase." )]
		public Light sunLight;

		static readonly int MoonPositionId = Shader.PropertyToID( "moonPosition" );
		static readonly int MoonLightColourId = Shader.PropertyToID( "moonLightColour" );

		Material material;
		[Header( "Debug" )]
		public Camera camTest;
		public float debug_dst;
		public bool freezeOrbit;

		public void UpdateOrbit( float monthT, EarthOrbit earth, bool geocentric )
		{
			transform.localScale = Vector3.one * size * ( ( Application.isPlaying ) ? 1 : 2 );

			if ( freezeOrbit )
			{
				return;
			}

			Vector3 xAxis = new Vector3( Mathf.Cos( moonOrbitAngle * Mathf.Deg2Rad ), Mathf.Sin( moonOrbitAngle * Mathf.Deg2Rad ), 0 );
			Vector3 yAxis = Vector3.forward;

			Vector2 orbitPos = Orbit.CalculatePointOnOrbit( periapsis, apoapsis, monthT );
			debug_dst = orbitPos.magnitude;
			Vector3 moonPos = ( xAxis * orbitPos.x + yAxis * orbitPos.y ) * dstMultiplier;
			Quaternion moonRot = Quaternion.Euler( 0, 0, -moonTilt ) * Quaternion.Euler( 0, -monthT * 360, 0 );

			// Earth object doesn't actually move/rotate, so have to move moon to account for that
			if ( geocentric )
			{
				transform.position = Quaternion.Inverse( earth.earthRot ) * moonPos;
				transform.rotation = Quaternion.Inverse( earth.earthRot ) * moonRot;
			}
			else
			{
				transform.position = earth.earthPos + moonPos;
				transform.rotation = moonRot;
			}

			PublishMoonLighting();

			if ( camTest )
			{
				camTest.transform.position = ( geocentric ) ? Vector3.zero : earth.earthPos;
				camTest.transform.LookAt( transform );
			}
		}

		void PublishMoonLighting()
		{
			Shader.SetGlobalVector( MoonPositionId, transform.position );

			Vector3 toMoon = transform.position.sqrMagnitude > 1e-6f
				? transform.position.normalized
				: Vector3.up;
			Vector3 toSun = sunLight != null ? -sunLight.transform.forward : Vector3.up;
			float phase = Mathf.Clamp01( ( 1f - Vector3.Dot( toMoon, toSun ) ) * 0.5f );

			Shader.SetGlobalVector( MoonLightColourId, (Vector4)( moonLightColour * moonLightIntensity * phase ) );
		}

		public void Setup( CommandBuffer cmd )
		{
			moonRenderer.gameObject.GetComponent<MeshFilter>().mesh = Seb.Meshing.IcoSphere.Generate( resolution ).ToMesh();
			Material mat = moonRenderer.sharedMaterial;

			cmd.DrawRenderer( moonRenderer, mat );
		}

	}

}
