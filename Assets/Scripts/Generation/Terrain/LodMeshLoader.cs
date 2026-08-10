using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Seb.Meshing;
using TerrainGeneration;

public class LodMeshLoader : MonoBehaviour
{
	public TextAsset meshFileHighRes;
	public TextAsset meshFileLowRes;

	public Material mat;
	public Material lowResMat;
	public bool useStaticBatching;
	public bool loadOnStart;
	public SimpleLodSystem lodSystem;

	[Header("Planet scales")]
	[Tooltip("One copy of the terrain per planet scale, so a scale can be selected instantly. " +
		"Empty, or a single 1, behaves exactly as before.")]
	public float[] planetScales = new float[] { 1 };

	[Tooltip("Base radius the meshes were baked at. Relief is measured against it.")]
	public TerrainHeightSettings heightSettings;

	/// <summary>One holder per entry in planetScales. Only the selected one is active.</summary>
	readonly List<GameObject> scaleHolders = new List<GameObject>();

	public int ScaleCount => scaleHolders.Count;

	void Start()
	{
		if (loadOnStart)
		{
			Load();
		}
	}

	/// <summary>
	/// Activates the copy built for `scale`, and reports whether one existed.
	///
	/// Selecting rather than rebuilding is the point: the meshes are pre-baked, so having several
	/// costs only memory and swapping is a SetActive.
	/// </summary>
	public bool SelectScale(float scale)
	{
		int index = -1;
		for (int i = 0; i < planetScales.Length && i < scaleHolders.Count; i++)
		{
			if (Mathf.Approximately(planetScales[i], scale)) { index = i; break; }
		}
		if (index < 0) { return false; }

		for (int i = 0; i < scaleHolders.Count; i++)
		{
			if (scaleHolders[i] != null) { scaleHolders[i].SetActive(i == index); }
		}
		return true;
	}

	public void Load()
	{
		float[] scales = planetScales != null && planetScales.Length > 0 ? planetScales : new float[] { 1 };

		for (int s = 0; s < scales.Length; s++)
		{
			// One holder per scale. Its transform stays at identity - the correction is baked into
			// the vertices, because the World root's uniform scale is applied on top of it.
			var holder = new GameObject($"Terrain (planet x{scales[s]:0.###})");
			holder.transform.SetParent(transform, worldPositionStays: false);
			holder.layer = gameObject.layer;
			scaleHolders.Add(holder);

			MeshRenderer[] highResRenderers = CreateRenderers(meshFileHighRes, mat, holder.transform, scales[s]);
			MeshRenderer[] lowResRenderers = CreateRenderers(meshFileLowRes, lowResMat, holder.transform, scales[s]);

			Debug.Assert(highResRenderers.Length == lowResRenderers.Length, "Mismatch in number of high and low res meshes");

			for (int i = 0; i < highResRenderers.Length; i++)
			{
				lodSystem.AddLOD(highResRenderers[i], lowResRenderers[i]);
			}

			holder.SetActive(s == 0);
		}
	}

	/// <summary>
	/// Rewrites vertices so that, once the World root scales everything by `planetScale`, terrain
	/// relief ends up at its authored world-unit height instead of scaling with the globe.
	///
	/// A uniform transform scales relief along with the radius, so a x16 planet got 48-unit
	/// mountains against an unchanged 8.8-unit atmosphere scale height - peaks towering out of the
	/// haze, and a camera at altitude 10 buried inside them. A real planet sixteen times larger
	/// does not have sixteen times taller mountains.
	///
	/// Pre-dividing the relief cancels it exactly: |v| = R0 + h becomes R0 + h/k, which the x k
	/// transform turns into R0*k + h. The globe grows, the mountains do not.
	///
	/// This needs no re-bake because it is the same arithmetic the offline generator would apply,
	/// to the same source data.
	/// </summary>
	static void ApplyReliefCorrection(SimpleMeshData mesh, float baseRadius, float planetScale)
	{
		if (Mathf.Approximately(planetScale, 1f)) { return; }

		Vector3[] vertices = mesh.vertices;
		for (int i = 0; i < vertices.Length; i++)
		{
			float radius = vertices[i].magnitude;
			if (radius <= 1e-5f) { continue; }

			float relief = radius - baseRadius;
			vertices[i] *= (baseRadius + relief / planetScale) / radius;
		}
	}

	MeshRenderer[] CreateRenderers(TextAsset loadFile, Material material, Transform parent, float planetScale)
	{
		SimpleMeshData[] meshData = MeshSerializer.BytesToMeshes(loadFile.bytes);

		float baseRadius = heightSettings != null ? heightSettings.worldRadius : 150f;
		foreach (SimpleMeshData data in meshData)
		{
			ApplyReliefCorrection(data, baseRadius, planetScale);
		}

		MeshRenderer[] meshRenderers = new MeshRenderer[meshData.Length];
		GameObject[] allObjects = new GameObject[meshData.Length];


		for (int i = 0; i < meshRenderers.Length; i++)
		{
			var renderObject = MeshHelper.CreateRendererObject(meshData[i].name, meshData[i], material, parent: parent, gameObject.layer);

			meshRenderers[i] = renderObject.renderer;
			allObjects[i] = renderObject.gameObject;

			if (useStaticBatching)
			{
				meshRenderers[i].gameObject.isStatic = true;
			}
		}

		if (useStaticBatching)
		{
			StaticBatchingUtility.Combine(allObjects, parent.gameObject);
		}

		return meshRenderers;
	}

}
