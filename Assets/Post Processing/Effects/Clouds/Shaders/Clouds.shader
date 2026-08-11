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
			#include "Assets/Post Processing/Effects/Clouds/Shader Common/CloudCommon.hlsl"
			// For getBlueNoise. Also brings the tone-map constants, which stage 4 needs and which
			// arrive as globals from AtmosphereEffect.BindGlobalResources.
			#include "Assets/Post Processing/Effects/Atmosphere/Shader Common/DrawAtmosphereCommon.hlsl"

			struct appdata
			{
				float4 vertex : POSITION;
				float4 uv : TEXCOORD0;
			};

			struct v2f
			{
				float4 pos : SV_POSITION;
				float2 uv : TEXCOORD0;
				float3 viewVector : TEXCOORD1;
			};

			v2f vert(appdata v)
			{
				v2f output;
				output.pos = UnityObjectToClipPos(v.vertex);
				output.uv = v.uv;
				// Left unnormalised on purpose: its length converts eye depth into distance along
				// the ray, which is what the shell segment has to be clipped against.
				float3 viewVector = mul(unity_CameraInvProjection, float4(v.uv.xy * 2 - 1, 0, -1));
				output.viewVector = mul(unity_CameraToWorld, float4(viewVector, 0));
				return output;
			}

			sampler2D _MainTex;
			sampler2D _CameraDepthTexture;

			// Target length of a march step, in world units. The step COUNT is then derived from
			// the segment rather than fixed, so a vertical ray through a 1.7-unit shell and a
			// grazing ray through a 45-unit chord are both sampled properly.
			//
			// The reference project instead hardcodes a step of 11 world units, which at this
			// planet's scale would step over the entire cloud layer six times in one step.
			float cloudStepSize;
			int cloudMinSteps;
			int cloudMaxSteps;
			float cloudJitterStrength;

			// Stage 3 stand-in. Replaced in stage 4 by sun transmittance, a light march,
			// Beer-Powder, phase and sky ambient.
			float3 cloudFlatColour;
			float cloudExtinction;

			float4 frag(v2f i) : SV_Target
			{
				float3 background = tex2D(_MainTex, i.uv).rgb;

				float3 rayOrigin = _WorldSpaceCameraPos;
				float viewLength = length(i.viewVector);
				float3 rayDir = i.viewVector / viewLength;

				float start, end;
				if (!cloudShellSegment(rayOrigin, rayDir, start, end))
				{
					return float4(background, 1);
				}

				// Scene geometry ends the march. Terrain rises above the sphere the shell is built
				// on, so depth - not a ground intersection - is what actually occludes cloud.
				float sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv)) * viewLength;
				end = min(end, sceneDepth);
				if (end <= start) { return float4(background, 1); }

				float segment = end - start;
				int steps = clamp((int)(segment / max(1e-4, cloudStepSize)), cloudMinSteps, cloudMaxSteps);
				float stepSize = segment / steps;

				// Blue-noise start offset, so undersampling breaks up into noise rather than into
				// the concentric banding a fixed phase produces on a spherical shell.
				float jitter = getBlueNoise(i.uv).r * cloudJitterStrength * stepSize;

				float transmittance = 1;

				for (int s = 0; s < steps; s++)
				{
					float t = start + (s + 0.5) * stepSize + jitter;
					if (t >= end) { break; }

					float density = sampleCloudDensity(rayOrigin + rayDir * t, false);

					if (density > 0)
					{
						transmittance *= exp(-density * stepSize * cloudExtinction);
						if (transmittance < 0.01) { break; }
					}
				}

				// Flat white for now. Silhouette and coverage are what stage 3 has to show - whether
				// the cloud-type axis produces recognisably different genera - and both read from
				// the shape alone.
				float3 col = background * transmittance + cloudFlatColour * (1 - transmittance);
				return float4(col, 1);
			}
			ENDCG
		}
	}
	Fallback Off
}
