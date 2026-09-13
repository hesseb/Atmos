#ifndef TONE_MAP_COMMON_INCLUDED
#define TONE_MAP_COMMON_INCLUDED

float intensity;
float contrast;
float whitePoint;

static const float BLACK_FLOOR = -0.05;
static const float BLACK_SOFT_FLOOR_WIDTH = 0.05;

// https://64.github.io/tonemapping/
// v is the value being compressed
float3 reinhard_extended(float3 v, float max_white)
{
	float3 numerator = v * (1.0f + (v / (max_white * max_white)));
	return numerator / (1.0f + v);
}

// a, b values being max'd
// k width of soft floor
float3 smoothMax (float3 a, float3 b, float k) {
	k = -abs (k);
	float3 h = saturate((b - a + k) / (2 * k));
	return a * h + b * (1 - h) - k * h * (1 - h);
}

float3 toneMap(float3 col) {
	col *= intensity;
	col = lerp(0.5, col, contrast);
	col = reinhard_extended(col, whitePoint);
	col = smoothMax(col, BLACK_FLOOR, BLACK_SOFT_FLOOR_WIDTH);

	return col;
}

#endif
