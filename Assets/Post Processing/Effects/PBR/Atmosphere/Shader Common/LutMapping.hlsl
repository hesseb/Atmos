// Texel-centre mapping shared by every atmosphere LUT, so bilinear sampling never extrapolates.

#ifndef LUT_MAPPING_INCLUDED
#define LUT_MAPPING_INCLUDED

// Clamped to size >= 2 to avoid a NaN when the LUT-size uniform is still unset.
float lutUnitToSubUv(float unit, float size) { size = max(size, 2.0); return 0.5 / size + unit * (1.0 - 1.0 / size); }
float lutSubUvToUnit(float uv, float size) { size = max(size, 2.0); return (uv - 0.5 / size) / (1.0 - 1.0 / size); }

float2 lutUnitToSubUv(float2 unit, float2 size) { size = max(size, 2.0); return 0.5 / size + unit * (1.0 - 1.0 / size); }
float2 lutSubUvToUnit(float2 uv, float2 size) { size = max(size, 2.0); return (uv - 0.5 / size) / (1.0 - 1.0 / size); }

#endif
