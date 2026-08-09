// Screen-space-width line segments for the hovered-country border highlight.
//
// Derived from Instanced/InstancedLines, with three changes:
//   1. Alpha blending, so the highlight can fade in. The original returns flat colour
//      with no Blend state, which makes alpha inert.
//   2. A horizon cull. This draws with ZTest Always (see CountryHighlight for why), so
//      without it the far side of a country's border draws straight through the planet
//      as a ghost outline.
//   3. A soft edge, for anti-aliasing and a slight glow falloff.
Shader "Game/Highlight Lines"
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

			// Terrain height, so the glow can sit on the ground rather than at sea level.
			sampler2D HeightMap;
			float heightMultiplier;

			// The country polygons are sea-level lon/lat, but the baked border meshes
			// follow the terrain (measured: a quarter of their vertices sit above 150.05,
			// up to 153.17). Left at sea level the glow drifts off the drawn border by
			// dr/tan(elevation) - negligible looking straight down, ~20 units at a
			// grazing angle over mountains. Lifting to the same height removes it.
			float3 raiseToTerrain(float3 p)
			{
				float3 dir = normalize(p);
				float h = tex2Dlod(HeightMap, float4(pointToUV(dir), 0, 0)).r;
				return dir * (globeRadius + h * heightMultiplier);
			}

			struct v2f
			{
				float4 pos : SV_POSITION;
				float across : TEXCOORD0; // -1..1 across the line width
			};

			// A point p on a sphere of radius R centred at the origin is visible from
			// camera position c exactly when dot(p, c) > R*R. If the camera is inside the
			// sphere (free-fly can do that) nothing is over the horizon.
			bool overHorizon(float3 p, float3 camPos, float r)
			{
				if (dot(camPos, camPos) <= r * r) { return false; }
				return dot(p, camPos) <= r * r;
			}

			v2f vert (appdata_full v, uint instanceID : SV_InstanceID)
			{
				v2f o;

				LineSegment segment = lineSegments[instanceID];

				// Horizon test on the sea-level points, since overHorizon assumes |p| = R.
				// Testing the raised points against R would cull a little early; that
				// errs toward hiding rather than letting geometry punch through the limb.
				bool hidden = overHorizon(segment.pointA, _WorldSpaceCameraPos, globeRadius)
					|| overHorizon(segment.pointB, _WorldSpaceCameraPos, globeRadius);

				float3 a = raiseToTerrain(segment.pointA);
				float3 b = raiseToTerrain(segment.pointB);

				// Slightly conservative - it drops segments straddling the horizon - which
				// reads more cleanly than letting half a segment sink through the planet.
				if (hidden)
				{
					// Collapse every vertex to one point: a zero-area triangle rasterises
					// nothing, regardless of clipping behaviour.
					o.pos = float4(0, 0, -2, 1);
					o.across = 0;
					return o;
				}

				float flipY = _ProjectionParams.x; // 1 or -1 (flipped depending on platform)
				float aspect = _ScreenParams.y / _ScreenParams.x;
				float4 clipPointA = mul(UNITY_MATRIX_VP, float4(a, 1.0f));
				float4 clipPointB = mul(UNITY_MATRIX_VP, float4(b, 1.0f));
				float2 screenPointA = (clipPointA.xy * float2(1, flipY) / clipPointA.w) * 0.5 + 0.5;
				float2 screenPointB = (clipPointB.xy * float2(1, flipY) / clipPointB.w) * 0.5 + 0.5;

				float2 screenLineOffset = screenPointB - screenPointA;
				float2 screenLineNormal = normalize(float2(-screenLineOffset.y, screenLineOffset.x));

				float2 screenVertPos = screenPointA + screenLineOffset * v.vertex.x
					+ screenLineNormal * float2(aspect, 1) * v.vertex.y * width;

				float4 clip = lerp(clipPointA, clipPointB, v.vertex.x);

				o.pos = float4((screenVertPos * 2 - 1) * float2(1, flipY) * clip.w, clip.z, clip.w);
				o.across = v.vertex.y * 2; // quad spans -0.5..0.5, remap to -1..1
				return o;
			}

			float4 frag (v2f i) : SV_Target
			{
				float edge = 1 - smoothstep(softness, 1.0, abs(i.across));
				return float4(colour.rgb, colour.a * edge);
			}

			ENDCG
		}
	}
}
