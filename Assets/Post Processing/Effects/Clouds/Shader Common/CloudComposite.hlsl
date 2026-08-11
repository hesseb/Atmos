// The cloud composite: blends a cloud pass over the frame, upsampling it when it was rendered at
// a lower resolution.
//
// Shared by the volumetric renderer and the baseline rather than duplicated into each, for the
// reason SkyPass.Record states about the sky: two renderers being compared must differ ONLY in the
// shading model. A copied composite would start identical and drift, and the drift would be
// measured as a difference between techniques - which is the one error this comparison cannot
// absorb. Sharing makes the identity structural instead of a claim.
//
// This is also what lets the Full and Half cost modes mean the same thing for both: the upsample
// filter, its depth rejection, and its fallback behaviour are literally the same code, so the gap
// between Full and Half is a property of the marching rather than of two upsamplers.
//
// The including pass must declare nothing itself - vertex stage, samplers and uniforms are all
// here. Include it inside a Pass's CGPROGRAM after UnityCG.cginc.

#ifndef CLOUD_COMPOSITE_INCLUDED
#define CLOUD_COMPOSITE_INCLUDED

struct compositeAppdata
{
	float4 vertex : POSITION;
	float4 uv : TEXCOORD0;
};

struct compositeV2f
{
	float4 pos : SV_POSITION;
	float2 uv : TEXCOORD0;
};

compositeV2f compositeVert(compositeAppdata v)
{
	compositeV2f output;
	output.pos = UnityObjectToClipPos(v.vertex);
	output.uv = v.uv;
	return output;
}

sampler2D _MainTex;
sampler2D _CloudTex;
sampler2D _CameraDepthTexture;

/// xy = cloud pass resolution, zw = its texel size.
float4 _CloudTexSize;
/// 1 when the cloud pass ran at full resolution, so the bilateral path is skipped.
float _CloudUpsample;
/// How hard to reject a neighbour across a depth discontinuity. Higher keeps silhouettes crisper
/// and lets more of the low-resolution stair-stepping through.
float _CloudDepthRejection;

/// -1 off, 0 above the shell, 1 inside it, 2 below. Computed on the CPU, because the camera
/// position and the shell radii are both known there and this only needs answering once per frame
/// rather than once per pixel.
float _CloudDebugRegion;

float cloudCompositeEyeDepth(float2 uv)
{
	return LinearEyeDepth(tex2Dlod(_CameraDepthTexture, float4(uv, 0, 0)).r);
}

/// Depth-aware upsample.
///
/// A plain bilinear stretch of a half-resolution pass bleeds cloud across every silhouette,
/// because the four neighbours it blends may sit on opposite sides of a depth discontinuity - a
/// mountain ridge against sky reads as a halo. Weighting each neighbour by how well its depth
/// matches this pixel's rejects the ones that belong to different geometry.
///
/// The neighbours' depths are read from the full-resolution depth buffer at the low-resolution
/// texel centres, which is exactly the depth each of those pixels used - so no second depth target
/// is needed.
float4 upsampleCloud(float2 uv)
{
	if (_CloudUpsample <= 1.0) { return tex2Dlod(_CloudTex, float4(uv, 0, 0)); }

	float2 coord = uv * _CloudTexSize.xy - 0.5;
	float2 base = floor(coord);
	float2 f = coord - base;

	float centreDepth = cloudCompositeEyeDepth(uv);

	float4 sum = 0;
	float weightSum = 0;

	[unroll]
	for (int y = 0; y < 2; y++)
	{
		[unroll]
		for (int x = 0; x < 2; x++)
		{
			float2 tapUv = (base + float2(x, y) + 0.5) * _CloudTexSize.zw;
			float bilinear = (x ? f.x : 1 - f.x) * (y ? f.y : 1 - f.y);
			float depthDelta = abs(cloudCompositeEyeDepth(tapUv) - centreDepth);
			float weight = bilinear / (1 + depthDelta * _CloudDepthRejection);

			sum += tex2Dlod(_CloudTex, float4(tapUv, 0, 0)) * weight;
			weightSum += weight;
		}
	}

	// Every neighbour can be rejected at once on a thin feature, so fall back to the nearest tap
	// rather than dividing by zero.
	if (weightSum < 1e-4) { return tex2Dlod(_CloudTex, float4(uv, 0, 0)); }
	return sum / weightSum;
}

float4 compositeFrag(compositeV2f i) : SV_Target
{
	float3 background = tex2D(_MainTex, i.uv).rgb;
	float4 cloud = upsampleCloud(i.uv);
	float3 col = background * cloud.a + cloud.rgb;

	// A corner swatch rather than a full-screen fill: which region the camera is in is only useful
	// if the clouds are still visible next to it, so the two can be correlated as the camera moves.
	if (_CloudDebugRegion >= 0 && i.uv.x < 0.05 && i.uv.y > 0.93)
	{
		if (_CloudDebugRegion < 0.5) { return float4(1, 0.1, 0.1, 1); }   // above
		if (_CloudDebugRegion < 1.5) { return float4(0.1, 1, 0.1, 1); }   // inside
		return float4(0.2, 0.4, 1, 1);                                     // below
	}

	return float4(col, 1);
}

#endif
