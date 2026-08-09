using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[System.Serializable]
public class CloudNoiseGenerator
{
    const int computeThreadGroupSize = 8;

    public int shapeResolution = 128;
    public int detailResolution = 32;

    [HideInInspector]
    public RenderTexture shapeTexture;
    [HideInInspector]
    public RenderTexture detailTexture;

    List<ComputeBuffer> buffersToRelease;

    public void UpdateNoise(ComputeShader noiseCompute, WorleyNoiseSettings[] shapeSettings, WorleyNoiseSettings[] detailSettings)
    {
        if (noiseCompute == null) return;

        CreateTexture(ref shapeTexture, shapeResolution, "CloudShapeNoise");
        CreateTexture(ref detailTexture, detailResolution, "CloudDetailNoise");

        GenerateWorleyNoise(noiseCompute, shapeTexture, shapeSettings);
        GenerateWorleyNoise(noiseCompute, detailTexture, detailSettings);
    }

    void GenerateWorleyNoise(ComputeShader noiseCompute, RenderTexture texture, WorleyNoiseSettings[] settingsArray)
    {
        if (settingsArray == null) return;

        int resolution = texture.width;

        for (int channel = 0; channel < settingsArray.Length && channel < 4; channel++)
        {
            var settings = settingsArray[channel];
            if (settings == null) continue;

            buffersToRelease = new List<ComputeBuffer>();

            Vector4 channelMask = Vector4.zero;
            channelMask[channel] = 1;

            noiseCompute.SetFloat("persistence", settings.persistence);
            noiseCompute.SetInt("resolution", resolution);
            noiseCompute.SetVector("channelMask", channelMask);
            noiseCompute.SetTexture(0, "Result", texture);

            var minMaxBuffer = CreateBuffer(noiseCompute, new int[] { int.MaxValue, 0 }, sizeof(int), "minMax", 0);

            // Create Worley point buffers
            var prng = new System.Random(settings.seed);
            CreateWorleyPointsBuffer(noiseCompute, prng, settings.numDivisionsA, "pointsA");
            CreateWorleyPointsBuffer(noiseCompute, prng, settings.numDivisionsB, "pointsB");
            CreateWorleyPointsBuffer(noiseCompute, prng, settings.numDivisionsC, "pointsC");

            noiseCompute.SetInt("numCellsA", settings.numDivisionsA);
            noiseCompute.SetInt("numCellsB", settings.numDivisionsB);
            noiseCompute.SetInt("numCellsC", settings.numDivisionsC);
            noiseCompute.SetBool("invertNoise", settings.invert);
            noiseCompute.SetInt("tile", settings.tile);

            // Dispatch noise generation
            int numThreadGroups = Mathf.CeilToInt(resolution / (float)computeThreadGroupSize);
            noiseCompute.Dispatch(0, numThreadGroups, numThreadGroups, numThreadGroups);

            // Dispatch normalization
            noiseCompute.SetBuffer(1, "minMax", minMaxBuffer);
            noiseCompute.SetTexture(1, "Result", texture);
            noiseCompute.Dispatch(1, numThreadGroups, numThreadGroups, numThreadGroups);

            // Release buffers
            foreach (var buffer in buffersToRelease)
            {
                buffer.Release();
            }
        }
    }

    void CreateWorleyPointsBuffer(ComputeShader compute, System.Random prng, int numCellsPerAxis, string bufferName)
    {
        var points = new Vector3[numCellsPerAxis * numCellsPerAxis * numCellsPerAxis];
        float cellSize = 1f / numCellsPerAxis;

        for (int x = 0; x < numCellsPerAxis; x++)
        {
            for (int y = 0; y < numCellsPerAxis; y++)
            {
                for (int z = 0; z < numCellsPerAxis; z++)
                {
                    float randomX = (float)prng.NextDouble();
                    float randomY = (float)prng.NextDouble();
                    float randomZ = (float)prng.NextDouble();
                    Vector3 randomOffset = new Vector3(randomX, randomY, randomZ) * cellSize;
                    Vector3 cellCorner = new Vector3(x, y, z) * cellSize;

                    int index = x + numCellsPerAxis * (y + z * numCellsPerAxis);
                    points[index] = cellCorner + randomOffset;
                }
            }
        }

        CreateBuffer(compute, points, sizeof(float) * 3, bufferName);
    }

    ComputeBuffer CreateBuffer(ComputeShader compute, System.Array data, int stride, string bufferName, int kernel = 0)
    {
        var buffer = new ComputeBuffer(data.Length, stride, ComputeBufferType.Structured);
        buffersToRelease.Add(buffer);
        buffer.SetData(data);
        compute.SetBuffer(kernel, bufferName, buffer);
        return buffer;
    }

    void CreateTexture(ref RenderTexture texture, int resolution, string name)
    {
        var format = GraphicsFormat.R16G16B16A16_UNorm;
        if (texture == null || !texture.IsCreated() || texture.width != resolution || texture.height != resolution || texture.volumeDepth != resolution || texture.graphicsFormat != format)
        {
            if (texture != null)
            {
                texture.Release();
            }
            texture = new RenderTexture(resolution, resolution, 0);
            texture.graphicsFormat = format;
            texture.volumeDepth = resolution;
            texture.enableRandomWrite = true;
            texture.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
            texture.name = name;
            texture.Create();
        }
        texture.wrapMode = TextureWrapMode.Repeat;
        texture.filterMode = FilterMode.Bilinear;
    }

    public void Release()
    {
        if (shapeTexture != null) shapeTexture.Release();
        if (detailTexture != null) detailTexture.Release();
    }
}
