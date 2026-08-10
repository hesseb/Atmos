// Half-texel inset, shared by every LUT in the atmosphere.
//
// A texture of N texels sampled with tex2D at coordinate u reads texel centre u*N - 0.5. So if a
// compute writes texel i as though it held the value for parameter i/(N-1), the write and the
// read agree only at u = 0.5 and differ by up to half a texel at the edges.
//
// That is not hypothetical either - it is what the transmittance LUT did, and what the aerial
// perspective did in depth, where half a slice was about 2.4 world units of misplaced fog.
//
// These map so that texel *centres* span the parameter domain exactly: unit 0 lands on the first
// texel's centre and unit 1 on the last, both endpoints exactly representable, and bilinear
// filtering never extrapolates past either.

#ifndef LUT_MAPPING_INCLUDED
#define LUT_MAPPING_INCLUDED

float lutUnitToSubUv(float unit, float size) { return 0.5 / size + unit * (1.0 - 1.0 / size); }
float lutSubUvToUnit(float uv, float size) { return (uv - 0.5 / size) / (1.0 - 1.0 / size); }

float2 lutUnitToSubUv(float2 unit, float2 size) { return 0.5 / size + unit * (1.0 - 1.0 / size); }
float2 lutSubUvToUnit(float2 uv, float2 size) { return (uv - 0.5 / size) / (1.0 - 1.0 / size); }

#endif
