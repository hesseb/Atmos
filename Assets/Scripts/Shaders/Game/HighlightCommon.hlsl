// Shared by Game/Highlight Lines and Game/Highlight Line Joins, so the two cannot drift
// apart in how they cull, displace or shade.
#ifndef HIGHLIGHT_COMMON_INCLUDED
#define HIGHLIGHT_COMMON_INCLUDED

#include "Assets/Scripts/Shader Common/GeoMath.hlsl"

struct LineSegment {
	float3 pointA;
	float3 pointB;
};

StructuredBuffer<LineSegment> lineSegments;

float width;
float4 colour;      // bright core
float4 haloColour;  // darker surround, for contrast against pale terrain
float coreFraction; // fraction of the half-width that stays core colour
float rimSoftness;  // how much of the outer edge fades to transparent
float globeRadius;

// Terrain height, so the glow sits on the ground rather than at sea level.
sampler2D HeightMap;
float heightMultiplier;

// A point p on a sphere of radius R centred at the origin is visible from camera position
// c exactly when dot(p, c) > R*R. If the camera is inside the sphere (free-fly can do
// that) nothing is over the horizon.
// Assumes |p| = R, so call it with sea-level points, not terrain-displaced ones.
bool overHorizon(float3 p, float3 camPos, float r)
{
	if (dot(camPos, camPos) <= r * r) { return false; }
	return dot(p, camPos) <= r * r;
}

// The country polygons are sea-level lon/lat, but the baked border meshes follow the
// terrain (measured: a quarter of their vertices sit above 150.05, up to 153.17). Left at
// sea level the glow drifts off the drawn border by dr/tan(elevation) - negligible
// looking straight down, ~20 units at a grazing angle over mountains.
float3 raiseToTerrain(float3 p)
{
	float3 dir = normalize(p);
	float h = tex2Dlod(HeightMap, float4(pointToUV(dir), 0, 0)).r;
	return dir * (globeRadius + h * heightMultiplier);
}

// t: 0 at the centre of the line, 1 at its outer edge.
//
// A bright core inside a darker halo, rather than one flat colour fading out. A single
// warm line is nearly invisible over pale desert; the halo guarantees an edge against
// whatever is underneath, while the soft outer falloff is what reads as glow.
float4 shadeHighlight(float t)
{
	float core = 1 - smoothstep(coreFraction * 0.55, max(coreFraction, 0.001), t);
	float coverage = 1 - smoothstep(1.0 - max(rimSoftness, 0.001), 1.0, t);

	float3 rgb = lerp(haloColour.rgb, colour.rgb, core);
	float alpha = coverage * lerp(haloColour.a, colour.a, core);
	return float4(rgb, alpha);
}

#endif
