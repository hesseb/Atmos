// Noise primitives shared by the offline volume bake and the per-frame weather map.
//
// One definition of the hash, because two would drift: the volumes and the weather map have to keep
// agreeing about where cloud is, and a divergence there is not a visible bug so much as a slow loss
// of whatever the parameters were tuned against.

#ifndef CLOUD_NOISE_COMMON_INCLUDED
#define CLOUD_NOISE_COMMON_INCLUDED

/// Schneider's remap, used throughout the density model as well as here.
float cloudRemap(float v, float oldMin, float oldMax, float newMin, float newMax)
{
	return newMin + (v - oldMin) / max(1e-5, oldMax - oldMin) * (newMax - newMin);
}

/// A unit gradient for a lattice corner.
///
/// rsqrt with a floor rather than normalize: a hash landing on the zero vector would make normalize
/// return NaN, and a single NaN texel propagates through everything downstream of it.
float3 cloudHashGradient(float3 cell)
{
	float3 h = float3(
		dot(cell, float3(127.1, 311.7, 74.7)),
		dot(cell, float3(269.5, 183.3, 246.1)),
		dot(cell, float3(113.5, 271.9, 124.6)));
	float3 g = -1.0 + 2.0 * frac(sin(h) * 43758.5453123);
	return g * rsqrt(max(1e-6, dot(g, g)));
}

/// Gradient noise on an unbounded domain. Roughly [-1, 1].
///
/// Used for the weather map, which is evaluated at points ON THE SPHERE and therefore has no wrap
/// to worry about - the domain is closed by the geometry rather than by the lattice.
float cloudGradientNoise(float3 p)
{
	float3 i0 = floor(p);
	float3 f = p - i0;
	float3 u = f * f * f * (f * (f * 6 - 15) + 10);

	float n = 0;

	[unroll]
	for (int cx = 0; cx <= 1; cx++)
	{
		[unroll]
		for (int cy = 0; cy <= 1; cy++)
		{
			[unroll]
			for (int cz = 0; cz <= 1; cz++)
			{
				float3 corner = float3(cx, cy, cz);
				float3 d = f - corner;
				float weight = lerp(1 - u.x, u.x, cx) * lerp(1 - u.y, u.y, cy) * lerp(1 - u.z, u.z, cz);
				n += dot(cloudHashGradient(i0 + corner), d) * weight;
			}
		}
	}

	return n;
}

float cloudFbm(float3 p, int octaves, float lacunarity, float gain)
{
	float sum = 0;
	float amplitude = 1;
	float norm = 0;

	for (int i = 0; i < octaves; i++)
	{
		sum += cloudGradientNoise(p) * amplitude;
		norm += amplitude;
		p *= lacunarity;
		amplitude *= gain;
	}

	return sum / max(1e-5, norm);
}

/// The same gradient, but with the lattice wrapped to `period` so the noise repeats exactly.
///
/// This wrap is the entire tiling mechanism for the baked volumes, which - unlike the weather map -
/// are sampled on an unbounded world-space domain and must wrap seamlessly. A break there does not
/// read as a subtle seam but as a straight line of cloud running the height of the sky.
float3 cloudLatticeGradient(int3 cell, int period)
{
	int3 c = ((cell % period) + period) % period;
	return cloudHashGradient((float3)c);
}

float cloudPerlinTileable(float3 pos, int period)
{
	float3 p = pos * period;
	int3 i0 = (int3)floor(p);
	float3 f = p - floor(p);
	float3 u = f * f * f * (f * (f * 6 - 15) + 10);

	float n = 0;

	[unroll]
	for (int cx = 0; cx <= 1; cx++)
	{
		[unroll]
		for (int cy = 0; cy <= 1; cy++)
		{
			[unroll]
			for (int cz = 0; cz <= 1; cz++)
			{
				int3 corner = int3(cx, cy, cz);
				float3 d = f - (float3)corner;
				float weight = lerp(1 - u.x, u.x, cx) * lerp(1 - u.y, u.y, cy) * lerp(1 - u.z, u.z, cz);
				n += dot(cloudLatticeGradient(i0 + corner, period), d) * weight;
			}
		}
	}

	return n;
}

float cloudPerlinFbmTileable(float3 pos, int period, int octaves, float gain)
{
	float sum = 0;
	float amplitude = 1;
	float norm = 0;
	int freq = period;

	for (int i = 0; i < octaves; i++)
	{
		sum += cloudPerlinTileable(pos, freq) * amplitude;
		norm += amplitude;
		amplitude *= gain;
		freq *= 2;   // doubling keeps every octave tiling on the same lattice
	}

	return sum / max(1e-5, norm);
}

#endif
