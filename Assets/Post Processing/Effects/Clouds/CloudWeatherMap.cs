using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[System.Serializable]
public class CloudWeatherMap
{
    public int resolution = 512;

    [HideInInspector]
    public RenderTexture weatherMap;

    List<ComputeBuffer> buffersToRelease;

    public void UpdateMap(ComputeShader noiseCompute, SimplexNoiseSettings noiseSettings, Vector2 heightOffset, Vector4 testParams)
    {
        if (noiseCompute == null || noiseSettings == null) return;

        CreateTexture(ref weatherMap, resolution);

        buffersToRelease = new List<ComputeBuffer>();

        var prng = new System.Random(noiseSettings.seed);
        var offsets = new Vector4[noiseSettings.numLayers];
        for (int i = 0; i < offsets.Length; i++)
        {
            var o = new Vector4((float)prng.NextDouble(), (float)prng.NextDouble(), (float)prng.NextDouble(), (float)prng.NextDouble());
            offsets[i] = (o * 2 - Vector4.one) * 1000;
        }
        CreateBuffer(noiseCompute, offsets, sizeof(float) * 4, "offsets");

        var settings = (SimplexNoiseSettings.DataStruct)noiseSettings.GetDataArray().GetValue(0);
        settings.offset += heightOffset;
        CreateBuffer(noiseCompute, new SimplexNoiseSettings.DataStruct[] { settings }, noiseSettings.Stride, "noiseSettings", 0);

        noiseCompute.SetTexture(0, "Result", weatherMap);
        noiseCompute.SetInt("resolution", resolution);
        var minMaxBuffer = CreateBuffer(noiseCompute, new int[] { int.MaxValue, 0 }, sizeof(int), "minMaxBuffer", 0);
        noiseCompute.SetBuffer(1, "minMaxBuffer", minMaxBuffer);
        noiseCompute.SetVector("minMax", Vector2.up); // (0, 1)
        noiseCompute.SetVector("params", testParams);

        int threadGroupSize = 16;
        int numThreadGroups = Mathf.CeilToInt(resolution / (float)threadGroupSize);
        noiseCompute.Dispatch(0, numThreadGroups, numThreadGroups, 1);

        // Release buffers
        foreach (var buffer in buffersToRelease)
        {
            buffer.Release();
        }
    }

    ComputeBuffer CreateBuffer(ComputeShader compute, System.Array data, int stride, string bufferName, int kernel = 0)
    {
        var buffer = new ComputeBuffer(data.Length, stride, ComputeBufferType.Raw);
        buffersToRelease.Add(buffer);
        buffer.SetData(data);
        compute.SetBuffer(kernel, bufferName, buffer);
        return buffer;
    }

    void CreateTexture(ref RenderTexture texture, int resolution)
    {
        var format = GraphicsFormat.R16G16B16A16_UNorm;
        if (texture == null || !texture.IsCreated() || texture.width != resolution || texture.height != resolution || texture.graphicsFormat != format)
        {
            if (texture != null)
            {
                texture.Release();
            }
            texture = new RenderTexture(resolution, resolution, 0);
            texture.graphicsFormat = format;
            texture.enableRandomWrite = true;
            texture.dimension = UnityEngine.Rendering.TextureDimension.Tex2D;
            texture.name = "CloudWeatherMap";
            texture.Create();
        }
        texture.wrapMode = TextureWrapMode.Repeat;
        texture.filterMode = FilterMode.Bilinear;
    }

    public void Release()
    {
        if (weatherMap != null) weatherMap.Release();
    }
}
