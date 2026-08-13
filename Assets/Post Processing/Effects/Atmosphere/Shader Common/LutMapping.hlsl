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

// Both forms are singular at two sizes, and both are reachable.
//
// At size 0 the inset is 0.5/0 = infinity, and every coordinate derived from it is infinity or NaN.
// That is not a hypothetical size: transmittanceLutSize, skyViewLutSize and multipleScatteringLutSize
// are all *uniforms*, so they read zero on any material or compute that has not had SetProperties
// run on it yet - and RenderingManager records the sky command buffer during its own OnEnable.
//
// At size 1 the span 1 - 1/size is zero, so the inverse divides by it. A one-texel LUT is a
// degenerate thing to ask for, but it should give a constant rather than a NaN.
//
// max(size, 2) rather than a guard at each call site, because there are nine of those and the
// failure they produce - a LUT read entirely out of domain - looks like a broken atmosphere rather
// than like an uninitialised uniform.
float lutUnitToSubUv(float unit, float size) { size = max(size, 2.0); return 0.5 / size + unit * (1.0 - 1.0 / size); }
float lutSubUvToUnit(float uv, float size) { size = max(size, 2.0); return (uv - 0.5 / size) / (1.0 - 1.0 / size); }

float2 lutUnitToSubUv(float2 unit, float2 size) { size = max(size, 2.0); return 0.5 / size + unit * (1.0 - 1.0 / size); }
float2 lutSubUvToUnit(float2 uv, float2 size) { size = max(size, 2.0); return (uv - 0.5 / size) / (1.0 - 1.0 / size); }

#endif
