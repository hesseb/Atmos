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
float4 colour;       // bright core
float4 haloColour;   // darker surround, for contrast against pale terrain
float coreFraction;  // 0..1 of the half-width held at core colour
float edgeSoftness;  // how soft the core-to-halo transition is
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
// A bright core with a darker halo fading out around it, rather than one flat colour. A
// single warm line is nearly invisible over pale desert; the halo guarantees an edge
// against whatever is underneath, while its falloff is what reads as glow.
//
// coreFraction is a fraction of the HALF-width, so the bright core is
// width * coreFraction pixels across. Keep it above ~0.5 or the core thins out to
// nothing and all that remains is a dark smudge.
float4 shadeHighlight(float t)
{
	float coreEnd = max(coreFraction, 0.02);
	float coreStart = coreEnd * (1 - saturate(edgeSoftness));

	float core = 1 - smoothstep(coreStart, coreEnd, t);
	// Halo runs from the core edge out to the rim, fading as it goes.
	float rim = 1 - smoothstep(coreEnd, 1.0, t);

	float3 rgb = lerp(haloColour.rgb, colour.rgb, core);
	float alpha = lerp(haloColour.a * rim, colour.a, core);
	return float4(rgb, alpha);
}

#endif
