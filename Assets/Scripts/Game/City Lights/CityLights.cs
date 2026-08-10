using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Seb.Meshing;

public class CityLights : MonoBehaviour
{
	public bool drawLights = true;
	public TextAsset cityLightsFile;

	public int meshRes;
	public Shader instanceShader;

	public Color colourDim;
	public Color colourBright;
	public float brightnessMultiplier = 1;
	public float sizeMin;
	public float sizeMax = 1;
	public float turnOnTimeVariation;
	public float turnOnTime;

	[Header("Debug")]
	[SerializeField, Disabled] Mesh mesh;

	CityLightRenderer[] renderers;

	Transform sunLight;

	CityLightGroup groups;
	ComputeBuffer cityLightBuffer;

	/// <summary>Lights and bounds exactly as baked, so a rescale never compounds.</summary>
	CityLight[] bakedLights;
	Bounds[] bakedBounds;
	float baseRadius = 150f;
	float planetScale = 1f;
	bool initialised;


	public void Init(RenderTexture heightMap, Light sunLight)
	{
		mesh = IcoSphere.Generate(meshRes, 0.5f).ToMesh();
		this.sunLight = sunLight.transform;

		CityLightGroup[] groups = CityLightGenerator.LoadFromFile(cityLightsFile);
		List<CityLight> lightsList = new List<CityLight>();

		for (int i = 0; i < groups.Length; i++)
		{
			lightsList.AddRange(groups[i].cityLights);
		}

		bakedLights = lightsList.ToArray();
		cityLightBuffer = ComputeHelper.CreateStructuredBuffer(lightsList);
		renderers = new CityLightRenderer[groups.Length];
		bakedBounds = new Bounds[groups.Length];
		for (int i = 0; i < groups.Length; i++) { bakedBounds[i] = groups[i].bounds; }

		int lightCountCumul = 0;
		for (int i = 0; i < groups.Length; i++)
		{
			int bufferOffset = lightCountCumul;
			int numInstancesInGroup = groups[i].cityLights.Length;
			lightCountCumul += numInstancesInGroup;

			renderers[i] = new CityLightRenderer(bufferOffset, numInstancesInGroup, groups[i].bounds, mesh, instanceShader);
			UpdateDynamicShaderProperties(renderers[i]);
			AssignConstantShaderData(renderers[i]);
		}

		// Init runs as a loading task, so a world scale may already have been chosen before the
		// lights existed. Applying it here covers that ordering as well as later swaps.
		initialised = true;
		ApplyPlanetScale();
	}



	/// <summary>
	/// Moves the lights onto a planet of radius `baseRadius * planetScale`.
	///
	/// The shader computes a light's world position as `pointOnSphere * height`, where the first
	/// is a unit direction and the second is an absolute radius - so this is the same correction
	/// the terrain gets, applied to a scalar: height above the surface is preserved while the
	/// surface itself moves.
	///
	/// Positions live in a ComputeBuffer drawn with DrawMeshInstancedIndirect, so no transform
	/// can move them - the buffer has to be rewritten. The per-group bounds are baked too, and
	/// they gate both the frustum cull and ShouldRender, so they scale with it or the night side
	/// simply stops drawing.
	/// </summary>
	public void SetPlanetScale(float baseRadius, float planetScale)
	{
		this.baseRadius = baseRadius;
		this.planetScale = planetScale;
		ApplyPlanetScale();
	}

	void ApplyPlanetScale()
	{
		if (!initialised || bakedLights == null) { return; }

		// Always from the baked values, never from the current ones, so repeated swaps cannot
		// compound into an ever-larger planet.
		var scaled = new CityLight[bakedLights.Length];
		for (int i = 0; i < bakedLights.Length; i++)
		{
			scaled[i] = bakedLights[i];
			scaled[i].height = baseRadius * planetScale + (bakedLights[i].height - baseRadius);
		}
		cityLightBuffer.SetData(scaled);

		for (int i = 0; i < renderers.Length && i < bakedBounds.Length; i++)
		{
			Bounds b = bakedBounds[i];
			renderers[i].bounds = new Bounds(b.center * planetScale, b.size * planetScale);
		}
	}

	void Update()
	{
		if (drawLights)
		{
			Vector3 dirToLight = -sunLight.forward;
			for (int i = 0; i < renderers.Length; i++)
			{
				if (renderers[i].ShouldRender(dirToLight))
				{
					UpdateDynamicShaderProperties(renderers[i]);
					renderers[i].Render();
				}
			}
		}
	}




	void UpdateDynamicShaderProperties(CityLightRenderer r)
	{
		r.material.SetVector(ShaderPropertyNames.dirToSunID, -sunLight.forward);
		// These should be constant at runtime, but update in editor for easy tweaking / recompiling
		if (Application.isEditor)
		{
			AssignConstantShaderData(r);
		}
	}

	void AssignConstantShaderData(CityLightRenderer r)
	{
		// Buffer
		r.material.SetBuffer("CityLights", cityLightBuffer);
		r.material.SetInt("bufferOffset", r.offset);
		// Settings
		r.material.SetColor("colourDim", colourDim);
		r.material.SetColor("colourBright", colourBright);
		r.material.SetFloat("brightnessMultiplier", brightnessMultiplier);
		r.material.SetFloat("sizeMin", sizeMin);
		r.material.SetFloat("sizeMax", sizeMax);
		r.material.SetFloat("turnOnTimeVariation", turnOnTimeVariation);
		r.material.SetFloat("turnOnTime", turnOnTime);
	}

	static class ShaderPropertyNames
	{
		public static int dirToSunID = Shader.PropertyToID("dirToSun");
	}

	void OnDestroy()
	{
		ComputeHelper.Release(cityLightBuffer);

		if (renderers != null)
		{
			foreach (var r in renderers)
			{
				r.Release();
			}
		}
	}


	public class CityLightRenderer
	{
		public ComputeBuffer renderArgs;
		public Bounds bounds;
		public Material material;
		public readonly int offset;
		Mesh mesh;
		int numInstances;

		public CityLightRenderer(int offset, int numInstances, Bounds bounds, Mesh mesh, Shader shader)
		{
			this.mesh = mesh;
			this.offset = offset;
			this.numInstances = numInstances;
			this.bounds = bounds;

			material = new Material(shader);
			renderArgs = ComputeHelper.CreateArgsBuffer(mesh, numInstances);
		}


		public void Release()
		{
			ComputeHelper.Release(renderArgs);
			Destroy(material);
		}

		public void Render()
		{
			var shadowMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			Graphics.DrawMeshInstancedIndirect(mesh, 0, material, bounds, renderArgs, camera: null, castShadows: shadowMode, receiveShadows: false);
		}

		// TODO: test/improve this
		public bool ShouldRender(Vector3 dirToSun)
		{
			var p = bounds.ClosestPoint(bounds.center - dirToSun * 1000);
			return Vector3.Dot(dirToSun, p.normalized) < 0.2f;
		}


	}
}
