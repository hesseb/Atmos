Shader "Hidden/Clouds"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "CloudCommon.hlsl"

            struct appdata {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 viewVector : TEXCOORD1;
            };

            v2f vert (appdata v) {
                v2f output;
                output.pos = UnityObjectToClipPos(v.vertex);
                output.uv = v.uv;
                float3 viewVector = mul(unity_CameraInvProjection, float4(v.uv * 2 - 1, 0, -1));
                output.viewVector = mul(unity_CameraToWorld, float4(viewVector, 0));
                return output;
            }

            sampler2D _MainTex;
            sampler2D _CameraDepthTexture;

            // March quality settings
            int maxSteps;
            float minStepSize;
            float rayOffsetStrength;

            // Light color from Unity
            float4 _LightColor0;

            float2 squareUV(float2 uv) {
                float width = _ScreenParams.x;
                float height = _ScreenParams.y;
                float scale = 1000;
                float x = uv.x * width;
                float y = uv.y * height;
                return float2(x / scale, y / scale);
            }

            float4 frag (v2f i) : SV_Target
            {
                // Create ray
                float3 rayOrigin = _WorldSpaceCameraPos;
                float viewLength = length(i.viewVector);
                float3 rayDir = i.viewVector / viewLength;

                // Scene depth
                float nonlin_depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                float depth = LinearEyeDepth(nonlin_depth) * viewLength;

                // Cloud shell intersection
                float2 shellHit = rayCloudShell(rayOrigin, rayDir);
                float dstToShell = shellHit.x;
                float dstThroughShell = shellHit.y;

                // Clamp by scene depth: don't render clouds behind terrain
                float dstLimit = min(depth - dstToShell, dstThroughShell);

                // No intersection or behind terrain
                if (dstLimit <= 0 || dstThroughShell <= 0) {
                    return tex2D(_MainTex, i.uv);
                }

                // Entry point into cloud shell
                float3 entryPoint = rayOrigin + rayDir * dstToShell;

                // Blue noise ray offset to reduce banding
                float randomOffset = BlueNoise.SampleLevel(samplerBlueNoise, squareUV(i.uv * 3), 0).r;
                randomOffset *= rayOffsetStrength;

                // Phase function: clouds brighter around sun
                float cosAngle = dot(rayDir, dirToSun);
                float phaseVal = phase(cosAngle);

                // Adaptive step size based on distance through shell
                float stepSize = max(minStepSize, dstLimit / (float)maxSteps);

                // March through cloud volume
                float transmittance = 1;
                float3 lightEnergy = 0;
                float dstTravelled = randomOffset;

                while (dstTravelled < dstLimit) {
                    float3 rayPos = entryPoint + rayDir * dstTravelled;
                    float density = sampleDensity(rayPos);

                    if (density > 0) {
                        float3 lightTransmittance = lightmarch(rayPos);
                        lightEnergy += density * stepSize * transmittance * lightTransmittance * phaseVal;
                        transmittance *= exp(-density * stepSize * lightAbsorptionThroughCloud);

                        // Early exit when nearly opaque
                        if (transmittance < 0.01) {
                            break;
                        }
                    }
                    dstTravelled += stepSize;
                }

                // Composite clouds with background (linear HDR, no tone mapping)
                float3 backgroundCol = tex2D(_MainTex, i.uv).rgb;
                float3 cloudCol = lightEnergy * _LightColor0.rgb;
                float3 col = backgroundCol * transmittance + cloudCol;

                return float4(col, 1);
            }

            ENDCG
        }
    }
}
