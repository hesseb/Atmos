// The cloud composite: blends a cloud pass over the frame, upsampling it when it was rendered at a lower resolution.

#ifndef CLOUD_COMPOSITE_INCLUDED
#define CLOUD_COMPOSITE_INCLUDED

sampler2D _MainTex;
sampler2D _CloudTex;
sampler2D _CameraDepthTexture;

float4 _CloudTexSize;
float _CloudUpsample;
float _CloudDepthRejection;
float _CloudDebugRegion;

///////// Internal helpers /////////////

float cloudCompositeEyeDepth(float2 uv)
{
	return LinearEyeDepth(tex2Dlod(_CameraDepthTexture, float4(uv, 0, 0)).r);
}

float4 upsampleCloud(float2 uv)
{
	if (_CloudUpsample <= 1.0)
	{
		return tex2Dlod(_CloudTex, float4(uv, 0, 0));
	}

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

	if (weightSum < 1e-4)
	{
		return tex2Dlod(_CloudTex, float4(uv, 0, 0));
	}

	return sum / weightSum;
}

///////// External entry points /////////////////

// Pair with UnityCG's vert_img.
float4 compositeFrag(v2f_img i) : SV_Target
{
	float3 background = tex2D(_MainTex, i.uv).rgb;
	float4 cloud = upsampleCloud(i.uv);
	float3 col = background * cloud.a + cloud.rgb;

	if (_CloudDebugRegion >= 0 && i.uv.x < 0.05 && i.uv.y > 0.93)
	{
		if (_CloudDebugRegion < 0.5)
		{
			return float4(1, 0.1, 0.1, 1); // above
		}

		if (_CloudDebugRegion < 1.5)
		{
			return float4(0.1, 1, 0.1, 1); // inside
		}

		return float4(0.2, 0.4, 1, 1); // below
	}

	return float4(col, 1);
}

#endif
