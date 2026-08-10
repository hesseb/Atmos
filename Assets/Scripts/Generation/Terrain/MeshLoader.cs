using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Seb.Meshing;

namespace TerrainGeneration
{
	public class MeshLoader : MonoBehaviour, IPlanetScaleSelectable
	{

		public TextAsset loadFile;
		public Material mat;
		public bool useStaticBatching;
		public bool loadOnStart;
		public bool disableLoading;

		[Header("Planet scales")]
		[Tooltip("One copy per planet scale, so a scale can be selected instantly. Empty, or a " +
			"single 1, behaves exactly as before.")]
		public float[] planetScales = new float[] { 1 };

		[Tooltip("Base radius this geometry was baked against. Height above it is the relief " +
			"that must NOT scale with the planet - country outlines sit at a small offset above " +
			"the surface, and scaling that offset leaves borders floating in the sky.")]
		public TerrainHeightSettings heightSettings;

		readonly List<GameObject> scaleHolders = new List<GameObject>();

		void Start()
		{
			if (loadOnStart)
			{
				Load();
			}
		}

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

		public LoadInfo Load()
		{
			if (disableLoading)
			{
				return default;
			}

			float[] scales = planetScales != null && planetScales.Length > 0 ? planetScales : new float[] { 1 };
			float baseRadius = heightSettings != null ? heightSettings.worldRadius : 150f;

			LoadInfo total = new LoadInfo();

			for (int s = 0; s < scales.Length; s++)
			{
				Transform parent = transform;

				// A single scale keeps the original hierarchy exactly, so nothing changes for
				// scenes that never swap.
				if (scales.Length > 1)
				{
					var holder = new GameObject($"{name} (planet x{scales[s]:0.###})");
					holder.transform.SetParent(transform, worldPositionStays: false);
					holder.layer = gameObject.layer;
					scaleHolders.Add(holder);
					parent = holder.transform;
				}

				LoadInfo info = Load(loadFile, mat, parent, useStaticBatching, gameObject.layer,
					baseRadius, scales[s]);

				total.vertexCount += info.vertexCount;
				total.numMeshes += info.numMeshes;
				total.loadDuration += info.loadDuration;

				if (scales.Length > 1) { scaleHolders[s].SetActive(s == 0); }
			}

			return total;
		}

		public static LoadInfo Load(TextAsset loadFile, Material material, Transform parent, bool useStaticBatching, int layer = 0,
			float baseRadius = 0f, float planetScale = 1f)
		{

			var sw = System.Diagnostics.Stopwatch.StartNew();
			LoadInfo info = new LoadInfo();

			SimpleMeshData[] meshData = MeshSerializer.BytesToMeshes(loadFile.bytes);

			GameObject[] allObjects = new GameObject[meshData.Length];

			for (int i = 0; i < meshData.Length; i++)
			{
				// Height above the base radius must survive the planet scale unchanged. Skipped
				// when baseRadius is 0, so the old five-argument callers behave as before.
				if (baseRadius > 0f) { PlanetRelief.Correct(meshData[i], baseRadius, planetScale); }

				var renderObject = MeshHelper.CreateRendererObject(meshData[i].name, meshData[i], material, parent: parent, layer: layer);

				allObjects[i] = renderObject.gameObject;
				if (useStaticBatching)
				{
					allObjects[i].gameObject.isStatic = true;
				}
				info.vertexCount += meshData[i].vertices.Length;
				info.numMeshes++;
			}

			if (useStaticBatching)
			{
				StaticBatchingUtility.Combine(allObjects, parent.gameObject);
			}

			info.loadDuration = sw.ElapsedMilliseconds;

			return info;
		}

		public struct LoadInfo
		{
			public int vertexCount;
			public int numMeshes;
			public long loadDuration;
		}
	}

}
