using UnityEngine;

[CreateAssetMenu(menuName = "PostProcessing/Clouds")]
public class CloudEffect : PostProcessingEffect
{
    [Header("References")]
    public AtmosphereEffect atmosphereEffect;

    [Header("Compute Shaders")]
    public ComputeShader noiseCompute;
    public ComputeShader weatherMapCompute;

    [Header("Cloud Shell Geometry")]
    public float cloudBaseAltitude = 20f;
    public float cloudTopAltitude = 60f;

    [Header("Base Shape")]
    public float cloudScale = 1f;
    public float densityMultiplier = 1f;
    public float densityOffset = 0f;
    public Vector4 shapeNoiseWeights = new Vector4(1, 0.5f, 0.25f, 0.125f);

    [Header("Detail")]
    public float detailNoiseScale = 10f;
    [Range(0, 1)]
    public float detailNoiseWeight = 0.1f;
    public Vector3 detailNoiseWeights = new Vector3(1, 0.5f, 0.25f);

    [Header("Height Gradient")]
    [Range(0, 1)]
    public float heightGradientMin = 0.2f;
    [Range(0, 1)]
    public float heightGradientMax = 0.7f;

    [Header("Lighting")]
    public float lightAbsorptionThroughCloud = 1f;
    public float lightAbsorptionTowardSun = 1f;
    [Range(0, 1)]
    public float darknessThreshold = 0.2f;
    [Range(0, 1)]
    public float forwardScattering = 0.83f;
    [Range(0, 1)]
    public float backScattering = 0.3f;
    [Range(0, 1)]
    public float baseBrightness = 0.8f;
    [Range(0, 1)]
    public float phaseFactor = 0.15f;

    [Header("Animation")]
    public float timeScale = 1f;
    public float baseSpeed = 1f;
    public float detailSpeed = 2f;
    public Vector3 shapeOffset;
    public Vector3 detailOffset;

    [Header("Quality")]
    public int numStepsLight = 6;
    public int maxSteps = 64;
    public float minStepSize = 0.5f;
    [Range(0, 1)]
    public float rayOffsetStrength = 1f;
    public Texture2D blueNoise;

    [Header("Weather Map")]
    public float weatherMapScale = 1f;
    [Range(0, 1)]
    public float weatherMapWeight = 0f;
    public Vector2 heightOffset;
    public Vector4 weatherTestParams = new Vector4(0, 1, 1, 0);

    [Header("Noise Settings")]
    public WorleyNoiseSettings[] shapeSettings;
    public WorleyNoiseSettings[] detailSettings;
    public SimplexNoiseSettings weatherSettings;
    public int shapeResolution = 128;
    public int detailResolution = 32;

    // Internal
    CloudNoiseGenerator noiseGenerator;
    CloudWeatherMap weatherMap;
    bool noiseInitialized;

    public override void OnEnable()
    {
        base.OnEnable();
        noiseInitialized = false;
    }

    void InitializeNoise()
    {
        if (noiseInitialized) return;

        noiseGenerator = new CloudNoiseGenerator();
        noiseGenerator.shapeResolution = shapeResolution;
        noiseGenerator.detailResolution = detailResolution;
        noiseGenerator.UpdateNoise(noiseCompute, shapeSettings, detailSettings);

        weatherMap = new CloudWeatherMap();
        weatherMap.UpdateMap(weatherMapCompute, weatherSettings, heightOffset, weatherTestParams);

        noiseInitialized = true;
    }

    protected override void RenderEffectToTarget(RenderTexture source, RenderTexture target)
    {
        if (atmosphereEffect == null || material == null)
        {
            Graphics.Blit(source, target);
            return;
        }

        InitializeNoise();

        // Apply shared atmosphere parameters (planetRadius, atmosphereThickness, dirToSun, etc.)
        var atmosValues = atmosphereEffect.GetShaderValues();
        atmosValues.Apply(material);

        // Set sun direction
        if (atmosphereEffect.Light != null)
        {
            material.SetVector("dirToSun", -atmosphereEffect.Light.transform.forward);
        }

        // Set transmittance LUT from atmosphere
        if (atmosphereEffect.transmittanceLUT != null)
        {
            material.SetTexture("TransmittanceLUT", atmosphereEffect.transmittanceLUT);
        }

        // Cloud shell radii
        float innerR = atmosphereEffect.bodyRadius + cloudBaseAltitude;
        float outerR = atmosphereEffect.bodyRadius + cloudTopAltitude;
        material.SetFloat("innerRadius", innerR);
        material.SetFloat("outerRadius", outerR);

        // Shape parameters
        material.SetFloat("cloudScale", cloudScale);
        material.SetFloat("densityMultiplier", densityMultiplier);
        material.SetFloat("densityOffset", densityOffset);
        material.SetVector("shapeNoiseWeights", shapeNoiseWeights);
        material.SetFloat("detailNoiseScale", detailNoiseScale);
        material.SetFloat("detailNoiseWeight", detailNoiseWeight);
        material.SetVector("detailWeights", detailNoiseWeights);

        // Height gradient
        material.SetFloat("heightGradientMin", heightGradientMin);
        material.SetFloat("heightGradientMax", heightGradientMax);

        // Lighting
        material.SetFloat("lightAbsorptionThroughCloud", lightAbsorptionThroughCloud);
        material.SetFloat("lightAbsorptionTowardSun", lightAbsorptionTowardSun);
        material.SetFloat("darknessThreshold", darknessThreshold);
        material.SetVector("phaseParams", new Vector4(forwardScattering, backScattering, baseBrightness, phaseFactor));

        // Animation
        material.SetFloat("timeScale", timeScale);
        material.SetFloat("baseSpeed", baseSpeed);
        material.SetFloat("detailSpeed", detailSpeed);
        material.SetVector("shapeOffset", shapeOffset);
        material.SetVector("detailOffset", detailOffset);

        // March quality
        material.SetInt("numStepsLight", Mathf.Max(1, numStepsLight));
        material.SetInt("maxSteps", Mathf.Max(4, maxSteps));
        material.SetFloat("minStepSize", Mathf.Max(0.01f, minStepSize));
        material.SetFloat("rayOffsetStrength", rayOffsetStrength);

        // Weather map
        material.SetFloat("weatherMapScale", weatherMapScale);
        material.SetFloat("weatherMapWeight", weatherMapWeight);

        // Blue noise
        if (blueNoise != null)
        {
            material.SetTexture("BlueNoise", blueNoise);
        }

        // Noise textures
        if (noiseGenerator != null)
        {
            if (noiseGenerator.shapeTexture != null)
                material.SetTexture("NoiseTex", noiseGenerator.shapeTexture);
            if (noiseGenerator.detailTexture != null)
                material.SetTexture("DetailNoiseTex", noiseGenerator.detailTexture);
        }

        if (weatherMap != null && weatherMap.weatherMap != null)
        {
            material.SetTexture("WeatherMap", weatherMap.weatherMap);
        }

        Graphics.Blit(source, target, material);
    }

    public override void OnDestroy()
    {
        noiseGenerator?.Release();
        weatherMap?.Release();
    }

    void OnValidate()
    {
        // Re-generate noise when settings change in editor
        noiseInitialized = false;
    }
}
