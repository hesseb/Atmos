Shader "Custom/Terrain"
{
	Properties
	{
		_ColWest("Colour West", 2D) = "white" {}
		_ColEast("Colour East", 2D) = "white" {}
		_NormalMapWest("Normal Map West", 2D) = "white" {}
		_NormalMapEast("Normal Map East", 2D) = "white" {}
		_LightMap("Light Map", 2D) = "white" {}
		_LakeMask("Lake Mask", 2D) = "white" {}

		[Header(Lighting)]
		_AmbientNight("Ambient Night", Color) = (0,0,0,0)
		_CityLightAmbient("City Light Ambient", Color) = (0,0,0,0)
		_FresnelCol("Fresnel Col", Color) = (0,0,0,0)
		_Contrast ("Contrast", Float) = 1
		_BrightnessAdd("Brightness Add", Float) = 0
		_BrightnessMul("Brightness Mul", Float) = 1
		_SkyAmbientWeight("Sky Ambient Weight", Range(0,2)) = 0.15
		_MoonLightWeight("Moon Light Weight", Range(0,4)) = 1

		[Header(Shadows)]
		_ShadowStrength("Shadow Strength", Range(0,1)) = 1
		_ShadowEdgeCol("Shadow Edge Col", Color) = (0,0,0,0)
		_ShadowInnerCol("Shadow Inner Col", Color) = (0,0,0,0)

		[Header(Lakes)]
		_Specular("Specular", Float) = 0
		[NoScaleOffset] _WaveNormalA ("Wave Normal A", 2D) = "bump" {}
		_WaveNormalScale ("Wave Normal Scale", Float) = 1
		_WaveStrength ("Wave Strength", Range(0, 1)) = 1

		[Header(Test)]
		_TestParams("Test Params", Vector) = (0,0,0,0)

	}
	SubShader
	{
		Pass
		{
			Tags { "LightMode" = "ForwardBase" "Queue" = "Geometry"}
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile_fwdbase
			// Country hover fill. A keyword rather than a branch so the extra texture
			// fetch is compiled out entirely when the country UI is off - this shader is
			// in the path the thesis measures.
			#pragma multi_compile __ COUNTRY_HIGHLIGHT_ON

			#include "UnityCG.cginc"
			#include "UnityLightingCommon.cginc"
			#include "AutoLight.cginc"

			#include "Assets/Scripts/Shader Common/GeoMath.hlsl"
			#include "Assets/Scripts/Shader Common/Triplanar.hlsl"

			// Sun and moon colour after travelling through the air, and sky radiance for an
			// arbitrary direction, from the atmosphere's transmittance and sky-view LUTs. Shared
			// with Ocean.shader so that land and water warm together at sunset - they meet at every
			// coastline, and a term applied to only one of them shows up as a seam.
			#include "Assets/Post Processing/Effects/Atmosphere/Shader Common/SurfaceLighting.hlsl"

			#if defined(COUNTRY_HIGHLIGHT_ON)
				// Set as globals by CountryHighlight, so deliberately NOT declared in
				// Properties - a material entry would shadow the global.
				sampler2D _CountryIndices;
				float _HighlightCountryIndex;
				float4 _HighlightFillColour;    // rgb tint, a strength
				float _HighlightFillBrightness;
			#endif

			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
				float3 normal : NORMAL;
			};

			struct v2f
			{
				float2 uv : TEXCOORD0;
				float4 pos : SV_POSITION;
				float3 worldNormal : NORMAL;
				float3 worldPos : TEXCOORD1;
				float4 screenPos : TEXCOORD2;
				LIGHTING_COORDS(4,5)
			};

			sampler2D _ColWest, _ColEast, _NormalMapWest, _NormalMapEast;
			sampler2D _LightMap, _LakeMask, _WaveNormalA;
			float4 _ColWest_TexelSize;

			float _ShadowStrength;
			float3 _ShadowEdgeCol, _ShadowInnerCol;
			float _WaveNormalScale, _WaveStrength;

			float _ShadingPow, _BrightnessAdd, _BrightnessMul, _Specular, _Contrast;
			float4 _AmbientNight, _CityLightAmbient, _FresnelCol;
			float4 _TestParams;

			float _SkyAmbientWeight, _MoonLightWeight;

			// Published by SolarSystem.Moon. A position, not a direction: at 811 world units against
			// a 150-unit planet the moon is not far enough away to be treated as directional.
			float4 moonPosition;
			float4 moonLightColour;   // already scaled by phase

			v2f vert (appdata v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.uv = v.uv;
				o.worldNormal = UnityObjectToWorldNormal(v.normal);
				o.worldPos =  mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1));
				o.screenPos = ComputeScreenPos(o.pos);
				TRANSFER_VERTEX_TO_FRAGMENT(o);
				return o;
			}

			float calculateSpecular(float3 normal, float3 viewDir, float3 sunDir, float smoothness) {
				float specularAngle = acos(dot(normalize(sunDir - viewDir), normal));
				float specularExponent = specularAngle / smoothness;
				float specularHighlight = exp(-max(0,specularExponent) * specularExponent);
				return specularHighlight;
			}


			float4 frag (v2f i) : SV_Target
			{
				float3 pointOnUnitSphere = normalize(i.worldPos);
				float2 texCoord = pointToUV(pointOnUnitSphere);
				float lightMap = tex2D(_LightMap, texCoord);
				float lakeMask = tex2D(_LakeMask, texCoord);

				float3 detailNormal = 0;
				float3 unlitTerrainCol = 0;
				if (texCoord.x < 0.5) {
					float2 tileTexCoord = float2(texCoord.x * 2, texCoord.y);
					float mipLevel = calculateGeoMipLevel(tileTexCoord, _ColWest_TexelSize.zw);
					unlitTerrainCol = tex2Dlod(_ColWest, float4(tileTexCoord, 0, mipLevel));
					detailNormal = tex2D(_NormalMapWest, tileTexCoord);
				}
				else {
					float2 tileTexCoord = float2((texCoord.x - 0.5) * 2, texCoord.y);
					float mipLevel = calculateGeoMipLevel(tileTexCoord, _ColWest_TexelSize.zw);
					unlitTerrainCol = tex2Dlod(_ColEast, float4(tileTexCoord, 0, mipLevel));
					detailNormal = tex2D(_NormalMapEast, tileTexCoord);
				}


				float3 meshWorldNormal = normalize(i.worldNormal);
				detailNormal = normalize(detailNormal * 2 - 1);
				// Blend detail normal with mesh normal
				float3 worldNormal = normalize(meshWorldNormal * 2 + detailNormal * 1.25);

				// Renamed from `dirToSun` to match Ocean.shader. AtmosphereCommon.hlsl declares a
				// global of that name; SurfaceLighting.hlsl deliberately does not pull it in, but a
				// shadowed uniform is the kind of thing that fails silently rather than loudly.
				float3 sunDir = _WorldSpaceLightPos0.xyz;

				// The colour the sun has by the time it reaches this point on the globe: white
				// overhead, deep orange at the terminator. This is what makes the warm band sweep
				// across the world with the sun, rather than the whole globe shifting hue with the
				// camera - which is what Sun.cs's gradient does, keyed on dot(dirToCam, dirToSun).
				//
				// Returns white when there is no physically based atmosphere bound, so the baseline
				// sky modes get back exactly the arithmetic this shader had before.
				// Cloud shadow multiplies the sun's colour, so it reaches the diffuse term and the
				// lake specular together and needs no other change in this shader. Applied here and
				// not inside sampleLightColour because that is also called with the moon's
				// direction, and the shadow map is baked for the sun.
				float3 sunColour = sampleLightColour(pointOnUnitSphere, sunDir) * cloudShadow(pointOnUnitSphere);
	
				float3 viewDir = normalize(i.worldPos - _WorldSpaceCameraPos.xyz);

				float3 waveA = triplanarNormal(_WaveNormalA, i.worldPos, pointOnUnitSphere, _WaveNormalScale, 0,_WaveStrength);
				float lakeSpecular = calculateSpecular(waveA, viewDir, sunDir, _Specular) * lakeMask;
				//return lakeSpecular;
				
				float shadows = LIGHT_ATTENUATION(i);
				float3 shadowCol = lerp(_ShadowEdgeCol, _ShadowInnerCol, saturate((1-shadows) * 1.5));
				shadows = lerp(1, shadows, _ShadowStrength);
				 
				float fakeLighting = pow(dot(worldNormal, pointOnUnitSphere), 3);
				
				// ---- Calculate night colour ----
				float nightShading = fakeLighting;
			
				float greyscale = dot(unlitTerrainCol, float3(0.299, 0.587, 0.114));
				float3 nightCol = (pow(greyscale, 0.67) * nightShading + nightShading * 0.3) * lerp(_AmbientNight * 0.1, _CityLightAmbient, saturate(lightMap * 1));
				float fresnel = saturate(1.5 * pow(1 + dot(viewDir, worldNormal), 5));
				nightCol += fresnel * _FresnelCol;

				// Moonlight. Added on top rather than folded into the tint above, so it lifts the
				// countryside without lifting the cities with it: that lerp is a single blended tint,
				// and scaling it would raise city and wilderness together and flatten the contrast
				// that makes the city lights read at all.
				//
				// moonLightColour arrives already scaled by phase, so a new moon contributes nothing.
				// The transmittance lookup does the horizon test for free - it returns exactly zero
				// once the moon is below the local horizon - so this appears and disappears with
				// moonrise and moonset, and reddens near the horizon the way the sun does.
				//
				// skyReflectionStrength gates it off in the baseline modes, where sampleLightColour
				// deliberately returns white rather than zero and so cannot gate it by itself.
				float3 moonDir = normalize(moonPosition.xyz - i.worldPos);
				float3 moonLight = saturate(dot(worldNormal, moonDir)) * moonLightColour.rgb
					* sampleLightColour(pointOnUnitSphere, moonDir) * _MoonLightWeight;
				nightCol += unlitTerrainCol * moonLight * skyReflectionStrength;

				float nightT = smoothstep(-0.25, 0.25, dot(pointOnUnitSphere, sunDir));
			
				
				// ---- Calculate day colour ----
				// Skylight landing on the surface. One lookup toward the local zenith, which is most
				// of what a flat-ish patch of ground sees. Land has never had this term at all -
				// _BrightnessAdd's 0.05 was the entire fill light, and being a scalar it carried no
				// colour, so shaded ground had no sky in it whatsoever.
				//
				// Multiplied by the albedo rather than added to the result, because that is what it
				// physically is: an irradiance the surface reflects. The ocean needs the opposite
				// treatment for a reason that does not apply here - _OceanCol is a baked
				// lit-appearance map rather than an albedo, so multiplying it squared its blue.
				float3 skyAmbient = sampleSkyViewSafe(pointOnUnitSphere, pointOnUnitSphere, sunDir) * _SkyAmbientWeight;

				float3 shading = saturate(saturate(dot(worldNormal, sunDir) + _BrightnessAdd)) * _BrightnessMul * sunColour;
				// The lake specular was a white scalar added straight to RGB - the same thing the
				// ocean glint was before it was given the sun's real colour. Same fix.
				float3 terrainCol = unlitTerrainCol * (shading + skyAmbient) + lakeSpecular * sunColour;
				// Apply shadows
				terrainCol = lerp(terrainCol, shadowCol, 1-shadows);
				// Adjust contrast
				terrainCol = lerp(0.5, terrainCol, _Contrast);
				terrainCol *= lerp(fakeLighting, 1, 0.5); // helps to make terrain look less flat and featureless when sun is directly overhead

				// ---- Interpolate between night and day for final colour ----
				float3 finalTerrainCol = lerp(nightCol, terrainCol, nightT);

				#if defined(COUNTRY_HIGHLIGHT_ON)
					if (_HighlightCountryIndex >= 0)
					{
						// Explicit mip 0 and a point-filtered texture: interpolating two
						// country indices would decode to a third, unrelated country.
						float encoded = tex2Dlod(_CountryIndices, float4(texCoord, 0, 0)).r;
						float index = floor(encoded * 255.0) - 1;

						if (abs(index - _HighlightCountryIndex) < 0.5)
						{
							float3 lit = finalTerrainCol * _HighlightFillColour.rgb * _HighlightFillBrightness;
							finalTerrainCol = lerp(finalTerrainCol, lit, _HighlightFillColour.a);
						}
					}
				#endif

				return float4(finalTerrainCol, 1);
			}
			ENDCG
		}
	}
	Fallback "VertexLit"
}
