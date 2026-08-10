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

		[Header("Lighting")]
		[Tooltip("Colour of moonlight for shaders that sample it. Moonlight is reflected sunlight " +
			"off a grey body, so it is close to neutral - the familiar blue cast is the eye, not " +
			"the light.")]
		public Color moonLightColour = new Color(0.85f, 0.89f, 1f);
		[Tooltip("Scales moonLightColour. Real moonlight is about a 400,000th of sunlight, which " +
			"no display transform here would survive, so this is an authored level.")]
		public float moonLightIntensity = 0.06f;
		[Tooltip("Needed for the moon's phase. Without it the moon reads as always full.")]
		public Light sunLight;

		static readonly int MoonPositionId = Shader.PropertyToID("moonPosition");
		static readonly int MoonLightColourId = Shader.PropertyToID("moonLightColour");

		Material material;
		[Header("Debug")]
		public Camera camTest;
		public float debug_dst;
		public bool freezeOrbit;




		void Start()
		{

			///moonMesh = sphere.GetMesh();
			//GetComponentInChildren<MeshFilter>().mesh = moonMesh;
			//material = GetComponentInChildren<MeshRenderer>().material;
		}

		public void UpdateOrbit(float monthT, EarthOrbit earth, bool geocentric)
		{

			transform.localScale = Vector3.one * size * ((Application.isPlaying) ? 1 : 2);

			if (freezeOrbit)
			{
				return;
			}

			Vector3 xAxis = new Vector3(Mathf.Cos(moonOrbitAngle * Mathf.Deg2Rad), Mathf.Sin(moonOrbitAngle * Mathf.Deg2Rad), 0);
			Vector3 yAxis = Vector3.forward;

			Vector2 orbitPos = Orbit.CalculatePointOnOrbit(periapsis, apoapsis, monthT);
			debug_dst = orbitPos.magnitude;
			Vector3 moonPos = (xAxis * orbitPos.x + yAxis * orbitPos.y) * dstMultiplier;
			Quaternion moonRot = Quaternion.Euler(0, 0, -moonTilt) * Quaternion.Euler(0, -monthT * 360, 0);

			// Earth object doesn't actually move/rotate, so have to move moon to account for that
			if (geocentric)
			{
				transform.position = Quaternion.Inverse(earth.earthRot) * moonPos;
				transform.rotation = Quaternion.Inverse(earth.earthRot) * moonRot;
			}
			else
			{
				transform.position = earth.earthPos + moonPos;
				transform.rotation = moonRot;
			}

			PublishMoonLighting();

			if (camTest)
			{
				camTest.transform.position = (geocentric) ? Vector3.zero : earth.earthPos;
				camTest.transform.LookAt(transform);
			}


			//Graphics.DrawMesh(moonMesh, Matrix4x4.TRS(transform.position, transform.GetChild(0).rotation, transform.localScale * scaleMul), moonMat, LayerMask.NameToLayer("ExtraTerrestrial"));
		}

		/// <summary>
		/// Publishes the moon's position and light colour for shaders that want to be lit by it.
		///
		/// The ocean is the first: it gives the moon a specular glint on the water the same way it
		/// does the sun. Position rather than direction, because at 811 world units against a
		/// 150-unit planet the moon is NOT far enough away to be directional - the direction to it
		/// swings by about ten degrees across the visible globe, which is exactly the scale a glint
		/// path is drawn at.
		///
		/// Worth knowing: the moon itself is not currently drawn. It orbits beyond the camera's
		/// 600-unit far plane, and its rendering is unfinished - so this lights the water without
		/// anything visible in the sky to justify it. Making the moon presentable is separate work.
		/// </summary>
		void PublishMoonLighting()
		{
			Shader.SetGlobalVector(MoonPositionId, transform.position);

			// Illuminated fraction as seen from the planet. The moon is opposite the sun at full,
			// so the dot is -1 there and +1 at new.
			Vector3 toMoon = transform.position.sqrMagnitude > 1e-6f
				? transform.position.normalized
				: Vector3.up;
			Vector3 toSun = sunLight != null ? -sunLight.transform.forward : Vector3.up;
			float phase = Mathf.Clamp01((1f - Vector3.Dot(toMoon, toSun)) * 0.5f);

			Shader.SetGlobalVector(MoonLightColourId, (Vector4)(moonLightColour * moonLightIntensity * phase));
		}

		public void Setup(CommandBuffer cmd)
		{
			//cmd.DrawMesh(mesh, Matrix4x4.TRS(new Vector3(70, 134, -80), Quaternion.identity, Vector3.one * 30), mat);
			moonRenderer.gameObject.GetComponent<MeshFilter>().mesh = Seb.Meshing.IcoSphere.Generate(resolution).ToMesh();
			Material mat = moonRenderer.sharedMaterial;

			cmd.DrawRenderer(moonRenderer, mat);
			//cmd.DrawMesh(moonMesh, Matrix4x4.TRS(Camera.main.transform.position + Camera.main.transform.forward * (10 + scaleMul), transform.GetChild(0).rotation, transform.localScale), moonMat);
		}

	}

}