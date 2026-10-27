// Noise primitives shared by the offline volume bake and the per-frame weather map.

#ifndef CLOUD_NOISE_COMMON_INCLUDED
#define CLOUD_NOISE_COMMON_INCLUDED

///////// Internal helpers /////////////

/// Hash from Quilez, "Noise - gradient - 3D" (shadertoy.com/view/Xsl3Dl, MIT).
float3 cloudHashGradient(float3 cell)
{
	float3 h = float3(
		dot(cell, float3(127.1, 311.7, 74.7)),
		dot(cell, float3(269.5, 183.3, 246.1)),
		dot(cell, float3(113.5, 271.9, 124.6)));
	float3 g = -1.0 + 2.0 * frac(sin(h) * 43758.5453123);
	return g * rsqrt(max(1e-6, dot(g, g)));
}

/// Quintic fade from Perlin, "Improving Noise" (SIGGRAPH 2002).
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

///////// External entry points /////////////////

/// Schneider's remap (SIGGRAPH 2015, GPU Pro 7 II-4).
float cloudRemap(float v, float oldMin, float oldMax, float newMin, float newMax)
{
	return newMin + (v - oldMin) / max(1e-5, oldMax - oldMin) * (newMax - newMin);
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

/// Period-wrapped Perlin FBM, as in Hillaire's TileableVolumeNoise (github.com/sebh/TileableVolumeNoise).
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
