// Round joins between the highlight line segments, so corners don't show notches.
// Companion to Game/Highlight Lines - same blending, horizon cull and soft edge.
Shader "Game/Highlight Line Joins"
{
	SubShader
	{
		Tags { "Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True" }

		Pass
		{
			Blend SrcAlpha OneMinusSrcAlpha
			ZWrite Off
			ZTest Always
			Cull Off
			Lighting Off

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 4.5

			#include "UnityCG.cginc"
			#include "Assets/Scripts/Shader Common/GeoMath.hlsl"

			struct LineSegment {
				float3 pointA;
				float3 pointB;
			};

			StructuredBuffer<LineSegment> lineSegments;
			float width;
			float4 colour;
			float globeRadius;
			float softness;

			sampler2D HeightMap;
			float heightMultiplier;

			// See Game/Highlight Lines - keeps the joins on the terrain with the segments.
			float3 raiseToTerrain(float3 p)
			{
				float3 dir = normalize(p);
				float h = tex2Dlod(HeightMap, float4(pointToUV(dir), 0, 0)).r;
				return dir * (globeRadius + h * heightMultiplier);
			}

			struct v2f
			{
				float4 pos : SV_POSITION;
				float radial : TEXCOORD0; // 0 at the centre, 1 at the rim
			};

			bool overHorizon(float3 p, float3 camPos, float r)
			{
				if (dot(camPos, camPos) <= r * r) { return false; }
				return dot(p, camPos) <= r * r;
			}

			v2f vert (appdata_full v, uint instanceID : SV_InstanceID)
			{
				v2f o;

				float3 seaLevelCentre = lineSegments[instanceID].pointA;

				// Horizon test on the sea-level point - overHorizon assumes |p| = R.
				if (overHorizon(seaLevelCentre, _WorldSpaceCameraPos, globeRadius))
				{
					o.pos = float4(0, 0, -2, 1);
					o.radial = 0;
					return o;
				}

				float3 worldCentre = raiseToTerrain(seaLevelCentre);

				float flipY = _ProjectionParams.x;
				float aspect = _ScreenParams.y / _ScreenParams.x;
				float4 clipCentre = mul(UNITY_MATRIX_VP, float4(worldCentre, 1));

				float2 screenCentre = (clipCentre.xy * float2(1, flipY) / clipCentre.w) * 0.5 + 0.5;
				float2 screenPos = screenCentre + v.vertex.xy * float2(aspect, 1) * width * 0.5;

				o.pos = float4((screenPos * 2 - 1) * clipCentre.w * float2(1, flipY), clipCentre.zw);
				// Join mesh is vertex 0 at the centre, the rest on the unit circle.
				o.radial = length(v.vertex.xy);
				return o;
			}

			float4 frag (v2f i) : SV_Target
			{
				float edge = 1 - smoothstep(softness, 1.0, i.radial);
				return float4(colour.rgb, colour.a * edge);
			}

			ENDCG
		}
	}
}
