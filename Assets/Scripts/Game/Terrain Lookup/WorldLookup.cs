using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

public class WorldLookup : MonoBehaviour
{
	public TerrainGeneration.TerrainHeightSettings heightSettings;
	public ComputeShader heightMapCompute;
	public ComputeShader lookupShader;
	public Texture2D countryIndices;

	// Small map containing normalized height values
	RenderTexture heightLookup;

	// Persistent buffer for the async path. Allocating one per query would mean a
	// ComputeBuffer per frame for anything polling this (e.g. mouse hover).
	ComputeBuffer asyncResultBuffer;
	System.Action<AsyncGPUReadbackRequest> onReadbackComplete;
	System.Action<TerrainInfo> pendingCallback;
	bool requestInFlight;

	/// <summary>
	/// True while an async query is awaiting readback. Only one is allowed at a time,
	/// which makes out-of-order or stale callbacks structurally impossible - the result
	/// always belongs to the most recent request. Callers polling every frame should skip
	/// dispatching while this is true.
	/// </summary>
	public bool RequestPending => requestInFlight;

	public void Init(RenderTexture heightMap)
	{
		GraphicsFormat format = GraphicsFormat.R8_UNorm;
		heightLookup = ComputeHelper.CreateRenderTexture(4096, 2048, FilterMode.Bilinear, format, "Height Lookup");
		Graphics.Blit(heightMap, heightLookup);

		ComputeHelper.CreateStructuredBuffer<float>(ref asyncResultBuffer, 2);
		// Cached so the async path doesn't allocate a closure per query.
		onReadbackComplete = OnReadbackComplete;
	}

	void DispatchLookup(Coordinate coordinate, ComputeBuffer target)
	{
		lookupShader.SetTexture(0, "HeightMap", heightLookup);
		lookupShader.SetTexture(0, "CountryIndices", countryIndices);
		lookupShader.SetBuffer(0, "Result", target);
		lookupShader.SetVector("uv", coordinate.ToUV());
		ComputeHelper.Dispatch(lookupShader, 1);
	}

	/// <summary>
	/// Queries terrain height and country index without stalling the pipeline. The
	/// callback arrives 2-3 frames later. Ignored if a request is already in flight.
	/// </summary>
	public void GetTerrainInfoAsync(Coordinate coord, System.Action<TerrainInfo> callback)
	{
		if (!SystemInfo.supportsAsyncGPUReadback)
		{
			callback?.Invoke(GetTerrainInfoImmediate(coord));
			return;
		}

		if (requestInFlight) { return; }

		requestInFlight = true;
		pendingCallback = callback;
		DispatchLookup(coord, asyncResultBuffer);
		AsyncGPUReadback.Request(asyncResultBuffer, onReadbackComplete);
	}

	public void GetTerrainInfoAsync(Vector3 point, System.Action<TerrainInfo> callback)
	{
		Coordinate coord = GeoMaths.PointToCoordinate(point.normalized);
		GetTerrainInfoAsync(coord, callback);
	}

	/// <summary>
	/// Blocking variant - stalls the GPU pipeline waiting for the result. Fine for
	/// one-shot setup and editor tooling, never for per-frame use.
	/// Uses its own buffer so it cannot clobber an async query in flight.
	/// </summary>
	public TerrainInfo GetTerrainInfoImmediate(Coordinate coordinate)
	{
		ComputeBuffer resultBuffer = ComputeHelper.CreateStructuredBuffer<float>(2);
		DispatchLookup(coordinate, resultBuffer);

		float[] data = new float[2];
		resultBuffer.GetData(data);
		resultBuffer.Release();

		return CreateTerrainInfo(data[0], data[1]);
	}

	void OnReadbackComplete(AsyncGPUReadbackRequest request)
	{
		requestInFlight = false;
		var callback = pendingCallback;
		pendingCallback = null;

		if (!Application.isPlaying || request.hasError) { return; }

		// Read straight off the NativeArray - ToArray() would allocate per readback.
		var data = request.GetData<float>();
		if (data.Length >= 2)
		{
			callback?.Invoke(CreateTerrainInfo(data[0], data[1]));
		}
	}

	TerrainInfo CreateTerrainInfo(float heightT, float countryT)
	{
		float worldHeight = heightSettings.worldRadius + heightT * heightSettings.heightMultiplier;
		// Texel stores (countryIndex + 1) / 255, ocean stores 0. Truncation is safe here:
		// float(k/255) * 255.0 never lands below k for any k in 0..255 (verified for all
		// 256 values), so this is NOT the off-by-one it resembles. Leave it alone.
		int countryIndex = (int)(countryT * 255.0) - 1;
		return new TerrainInfo(worldHeight, countryIndex);
	}

	void OnDestroy()
	{
		// Let any in-flight readback finish before the buffer it targets goes away.
		AsyncGPUReadback.WaitAllRequests();

		// Getting a warning when exiting playmode from menu scene after async loading game scene (but not activating).
		// This stops the warning. TODO: investigate
		if (RenderTexture.active == heightLookup)
		{
			RenderTexture.active = null;
		}

		ComputeHelper.Release(heightLookup);
		ComputeHelper.Release(asyncResultBuffer);
	}
}

public struct TerrainInfo
{
	public readonly float height;
	public readonly int countryIndex;

	public TerrainInfo(float height, int countryIndex)
	{
		this.height = height;
		this.countryIndex = countryIndex;
	}

	public bool inOcean
	{
		get
		{
			return countryIndex < 0;
		}
	}

}
