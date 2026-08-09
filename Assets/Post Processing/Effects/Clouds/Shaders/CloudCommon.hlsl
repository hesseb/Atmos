// Shared cloud rendering functions for spherical cloud shell raymarching.
// Includes atmosphere common for raySphere() and getSunTransmittanceLUT().

#include "../../Atmosphere/Shader Common/AtmosphereCommon.hlsl"

// Cloud shell parameters
float innerRadius; // planetRadius + cloudBaseAltitude
float outerRadius; // planetRadius + cloudTopAltitude

// Noise textures
Texture3D<float4> NoiseTex;
Texture3D<float4> DetailNoiseTex;
Texture2D<float4> WeatherMap;
Texture2D<float4> BlueNoise;

SamplerState samplerNoiseTex;
SamplerState samplerDetailNoiseTex;
SamplerState samplerWeatherMap;
SamplerState samplerBlueNoise;

// Transmittance LUT from atmosphere system
sampler2D TransmittanceLUT;

// Shape settings
float cloudScale;
float densityMultiplier;
float densityOffset;
float4 shapeNoiseWeights;
float detailNoiseScale;
float detailNoiseWeight;
float3 detailWeights;

// Height gradient
float heightGradientMin;
float heightGradientMax;

// Animation
float3 shapeOffset;
float3 detailOffset;
float timeScale;
float baseSpeed;
float detailSpeed;

// Lighting
float lightAbsorptionTowardSun;
float lightAbsorptionThroughCloud;
float darknessThreshold;
float4 phaseParams; // x: forward, y: back, z: baseBrightness, w: phaseFactor

// March settings
int numStepsLight;

// Weather map
float weatherMapScale;
float weatherMapWeight;

// ---- Utility functions ----

float remap(float v, float minOld, float maxOld, float minNew, float maxNew) {
    return minNew + (v - minOld) * (maxNew - minNew) / (maxOld - minOld);
}

// ---- Ray-Cloud Shell Intersection ----
// Returns float2(distanceToShellEntry, distanceThroughShell)
// Handles camera above, between, and below the cloud shell.
float2 rayCloudShell(float3 rayOrigin, float3 rayDir) {
    float2 outerHit = raySphere(0, outerRadius, rayOrigin, rayDir);
    float2 innerHit = raySphere(0, innerRadius, rayOrigin, rayDir);

    // Ray misses outer sphere entirely
    if (outerHit.y <= 0) return float2(1e20, 0);

    float camHeight = length(rayOrigin);
    float dstToEntry, dstToExit;

    if (camHeight > outerRadius) {
        // Camera above cloud shell: enter at outer near, exit at inner near or outer far
        dstToEntry = outerHit.x;
        dstToExit = (innerHit.y > 0) ? innerHit.x : (outerHit.x + outerHit.y);
    } else if (camHeight > innerRadius) {
        // Camera between shells: start immediately
        dstToEntry = 0;
        dstToExit = (innerHit.y > 0) ? innerHit.x : (outerHit.x + outerHit.y);
    } else {
        // Camera below inner shell (on/near planet surface)
        // Enter at inner sphere far side, exit at outer sphere far side
        float innerFar = innerHit.x + innerHit.y;
        float outerFar = outerHit.x + outerHit.y;
        if (innerFar <= 0) return float2(1e20, 0);
        dstToEntry = innerFar;
        dstToExit = outerFar;
    }

    float dstThroughShell = max(0, dstToExit - dstToEntry);
    return float2(max(0, dstToEntry), dstThroughShell);
}

// ---- Triplanar Sampling ----
// Sample a 2D texture on a sphere using triplanar projection
float triplanarScalar(float3 worldPos, float3 normal, float scale) {
    float3 scaledPos = worldPos * scale;
    float3 blend = normal * normal;
    blend /= dot(blend, 1);

    float valX = WeatherMap.SampleLevel(samplerWeatherMap, scaledPos.zy, 0).r;
    float valY = WeatherMap.SampleLevel(samplerWeatherMap, scaledPos.xz, 0).r;
    float valZ = WeatherMap.SampleLevel(samplerWeatherMap, scaledPos.xy, 0).r;

    return valX * blend.x + valY * blend.y + valZ * blend.z;
}

// ---- Cloud Density Sampling ----
float sampleDensity(float3 rayPos) {
    const int mipLevel = 0;
    const float baseScale = 1.0 / 1000.0;
    const float offsetSpeed = 1.0 / 100.0;

    float time = _Time.x * timeScale;

    // Spherical height calculation
    float altitude = length(rayPos) - innerRadius;
    float shellThickness = outerRadius - innerRadius;
    float heightPercent = saturate(altitude / shellThickness);

    // Height gradient (same shape as flat-world version)
    float heightGradient = saturate(remap(heightPercent, 0.0, heightGradientMin, 0, 1))
                         * saturate(remap(heightPercent, 1.0, heightGradientMax, 0, 1));

    // Weather map modulation (triplanar on sphere)
    if (weatherMapWeight > 0) {
        float3 sphereNormal = normalize(rayPos);
        float weather = triplanarScalar(rayPos, sphereNormal, weatherMapScale);
        heightGradient *= lerp(1.0, weather, weatherMapWeight);
    }

    // Noise UV: sample 3D noise directly at world position (tiles seamlessly)
    float3 uvw = rayPos * baseScale * cloudScale;
    float3 shapeSamplePos = uvw + shapeOffset * offsetSpeed + float3(time, time * 0.1, time * 0.2) * baseSpeed;

    // Base shape density
    float4 shapeNoise = NoiseTex.SampleLevel(samplerNoiseTex, shapeSamplePos, mipLevel);
    float4 normalizedShapeWeights = shapeNoiseWeights / dot(shapeNoiseWeights, 1);
    float shapeFBM = dot(shapeNoise, normalizedShapeWeights) * heightGradient;
    float baseShapeDensity = shapeFBM + densityOffset * 0.1;

    // Detail noise erosion (only if base shape has density)
    if (baseShapeDensity > 0) {
        float3 detailSamplePos = uvw * detailNoiseScale + detailOffset * offsetSpeed + float3(time * 0.4, -time, time * 0.1) * detailSpeed;
        float4 detailNoise = DetailNoiseTex.SampleLevel(samplerDetailNoiseTex, detailSamplePos, mipLevel);
        float3 normalizedDetailWeights = detailWeights / dot(detailWeights, 1);
        float detailFBM = dot(detailNoise.rgb, normalizedDetailWeights);

        float oneMinusShape = 1 - shapeFBM;
        float detailErodeWeight = oneMinusShape * oneMinusShape * oneMinusShape;
        float cloudDensity = baseShapeDensity - (1 - detailFBM) * detailErodeWeight * detailNoiseWeight;

        return cloudDensity * densityMultiplier * 0.1;
    }
    return 0;
}

// ---- Light March ----
// March toward the sun to determine how much light reaches a cloud point.
// Returns RGB because atmospheric transmittance is wavelength-dependent.
float3 lightmarch(float3 position) {
    // Distance from this point to the outer shell boundary toward the sun
    float dstToExit = raySphere(0, outerRadius, position, dirToSun).y;

    float stepSize = dstToExit / numStepsLight;
    float totalDensity = 0;

    float3 marchPos = position;
    for (int step = 0; step < numStepsLight; step++) {
        marchPos += dirToSun * stepSize;
        totalDensity += max(0, sampleDensity(marchPos) * stepSize);
    }

    // Cloud self-shadowing via Beer's law
    float cloudTransmittance = exp(-totalDensity * lightAbsorptionTowardSun);
    float lightReaching = darknessThreshold + cloudTransmittance * (1 - darknessThreshold);

    // Atmospheric transmittance: how much sunlight reaches this altitude after passing through atmosphere
    float3 atmosTransmittance = getSunTransmittanceLUT(TransmittanceLUT, position, dirToSun);

    return lightReaching * atmosTransmittance;
}

// ---- Phase Function ----
// Dual-lobe Henyey-Greenstein for cloud scattering
float hg(float cosAngle, float g) {
    float g2 = g * g;
    return (1 - g2) / (4 * 3.14159265 * pow(1 + g2 - 2 * g * cosAngle, 1.5));
}

float phase(float cosAngle) {
    float blend = 0.5;
    float hgBlend = hg(cosAngle, phaseParams.x) * (1 - blend) + hg(cosAngle, -phaseParams.y) * blend;
    return phaseParams.z + hgBlend * phaseParams.w;
}
