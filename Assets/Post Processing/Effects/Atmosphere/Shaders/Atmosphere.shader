Shader "Hidden/Atmosphere"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
	}
	SubShader
	{
		// No culling or depth
		Cull Off ZWrite Off ZTest Always

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag

			#include "UnityCG.cginc"
			#include "../Shader Common/AtmosphereCommon.hlsl"
			#include "../Shader Common/DrawAtmosphereCommon.hlsl"

			struct appdata {
					float4 vertex : POSITION;
					float4 uv : TEXCOORD0;
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
					float3 viewVector = mul(unity_CameraInvProjection, float4(v.uv.xy * 2 - 1, 0, -1));
					output.viewVector = mul(unity_CameraToWorld, float4(viewVector,0));
					return output;
			}

			sampler2D _MainTex;
			float4 _MainTex_TexelSize;
			sampler2D _CameraDepthTexture;

			sampler3D AerialPerspectiveLUT;
			sampler3D TransmittanceLUT3D;

			float4 params;
			float aerialPerspectiveStrength;

			// Remap a value from the range [minOld, maxOld] to [0, 1]
			float remap01(float minOld, float maxOld, float val) {
				return saturate((val - minOld) / (maxOld - minOld));
			}

			float3 getAtmoCol(float2 uv, float3 originalCol, float viewLength, float3 viewDir) {
				float3 outputCol = originalCol;
				float nonlin_depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);

				// Eye-space depth is kept as well as the radial distance, because the sky test has
				// to be made on the former.
				float eyeDepth = LinearEyeDepth(nonlin_depth);
				float sceneDepth = eyeDepth * viewLength;

				float nearClipPlane = _ProjectionParams.y;
				float farClipPlane = _ProjectionParams.z;
			
				float3 rayOrigin = _WorldSpaceCameraPos;
				float3 rayDir = viewDir;


				float2 hitInfo = raySphere(0, atmosphereRadius, rayOrigin, rayDir);
				float dstToAtmosphere = hitInfo.x;
				float dstThroughAtmosphere = hitInfo.y;

				// Sky is decided on eye depth, not on the radial distance, and with a tolerance.
				//
				// sceneDepth is eyeDepth * viewLength, and viewLength is exactly 1 at the centre of
				// the screen and greater everywhere else. So for an empty sky pixel the comparison
				// clears the far plane comfortably off-centre but lands exactly on it at the
				// centre, where float precision decides which way it goes. The pixels that fall
				// through are a disc in the middle of the screen, which then takes the aerial
				// perspective branch and gets fogged - a small translucent grey circle, fixed to
				// the centre, visible only against sky.
				//
				// It shows up at large far planes because LinearEyeDepth loses precision there, so
				// the reconstructed value falls short of the far plane more often. That is why it
				// appeared on the bigger planet scales, whose cull distance scales with them.
				if (eyeDepth >= farClipPlane * 0.999) {
					// Sky
				}
				// View ray goes through atmosphere (and not blocked by anything in front of it)
				else if (dstThroughAtmosphere > 0 && dstToAtmosphere < sceneDepth) {
					float3 inPoint = rayOrigin + rayDir * (dstToAtmosphere);
					float3 outPoint = rayOrigin + rayDir * min(dstToAtmosphere + dstThroughAtmosphere, sceneDepth);

					// The depth axis spans the air in front of THIS ray, not a fixed distance.
					//
					// Two fixed-range attempts failed for opposite reasons. A far distance of
					// bodyRadius left the whole visible scene inside five of the thirty-two slices
					// at low altitude; making the range quadratic and camera-dependent fixed that
					// but still gave only four slices from orbit, because it concentrates samples
					// near the camera and from out there the air is all far away.
					//
					// Normalising by the atmosphere chord alone does not work either: a ray looking
					// straight down is inside the atmosphere sphere, so its chord runs through the
					// planet and out the far side - 322 units of "atmosphere" for 2 units of air.
					//
					// Clipping that chord at the ground is what makes it exact. The span is then
					// always the air actually between the camera and what it is looking at, so all
					// thirty-two slices are used at every altitude. The compute derives the same
					// span from the same two intersections.
					float dstToGround = rayIntersectSphere(inPoint, rayDir, planetRadius);
					float span = dstToGround > 0 ? min(dstThroughAtmosphere, dstToGround) : dstThroughAtmosphere;

					// Capped at the tangent distance, identically to the compute, so the depth axis
					// is continuous across the horizon. See the long note in AerialPerspective.compute:
					// without it, adjacent texels either side of the horizon differ in span by ~4.8x,
					// and bilinear interpolation of that jump paints a serrated bright band along the
					// horizon. Ground rays are untouched - they always reach the planet at or before
					// the tangent - so this only truncates rays that miss, which are sky.
					float horizonDst = sqrt(max(0, dot(inPoint, inPoint) - planetRadius * planetRadius));
					span = min(span, horizonDst);

					float depthT = saturate((sceneDepth - dstToAtmosphere) / max(1e-5, span));

					float3 transmittance = tex3Dlod(TransmittanceLUT3D, float4(uv, depthT, 0)).rgb;
					float3 luminance = tex3Dlod(AerialPerspectiveLUT, float4(uv,depthT, 0)).rgb;
					
					luminance = toneMap(luminance);
					luminance = originalCol.rgb * transmittance + luminance;

					outputCol = blueNoiseDither(luminance, uv, ditherStrength);
					outputCol = lerp(originalCol.rgb, outputCol, aerialPerspectiveStrength);
					
				}

				return outputCol;
			}

			float4 frag (v2f i) : SV_Target
			{	
				float4 originalCol = tex2D(_MainTex, i.uv);

				float viewLength = length(i.viewVector);
				float3 viewDir = i.viewVector / viewLength;
			
				float3 c = getAtmoCol(i.uv, originalCol, viewLength, viewDir);

				return float4(c, 1);
			}
			ENDCG
		}
	}
}