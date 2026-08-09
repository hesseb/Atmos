// Round joins between the highlight line segments, so corners don't show notches.
// Companion to Game/Highlight Lines - shares its culling, terrain displacement and
// shading via HighlightCommon.hlsl.
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
			#include "HighlightCommon.hlsl"

			struct v2f
			{
				float4 pos : SV_POSITION;
				float radial : TEXCOORD0; // 0 at the centre, 1 at the rim
			};

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
				return shadeHighlight(i.radial);
			}

			ENDCG
		}
	}
}
