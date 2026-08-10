Shader "Custom/Ocean"
{
	Properties
	{
		_OceanCol("Ocean Colour", 2D) = "white" {}
		_Noise ("Noise", 2D) = "white" {}

		_SpecularSmoothness ("Specular Smoothness", Float) = 0
		_WaveNormalScale ("Wave Normal Scale", Float) = 1
		_WaveStrength ("Wave Strength", Range(0, 1)) = 1
		_WaveSpeed ("Wave Speed", Float) = 1
		[NoScaleOffset] _WaveNormalA ("Wave Normal A", 2D) = "bump" {}
		[NoScaleOffset] _WaveNormalB ("Wave Normal B", 2D) = "bump" {}

		_Refraction ("Refraction", Float) = 0
		_ShadowStrength ("Shadow Strength", Range(0,1)) = 1

		// Multiplies the baked ocean colour map. Was declared and never read.
		_Tint("Tint", Color) = (1,1,1,1)
		// 1 keeps the map as baked, 0 is fully greyscale. A tint can only darken, so this is
		// what actually takes saturation out.
		_Saturation("Saturation", Range(0,1)) = 1
		_Specular("Specular", Float) = 0
		// NIGHT-ONLY. In daylight the silhouette is handled by the physical Fresnel reflection, so
		// this fades to nothing above the terminator - changing it will look like it does nothing
		// unless the sun is down. It exists because the sky view LUT has no moonlight or starlight.
		_FresnelCol("Night Rim Colour", Color) = (0,0,0,0)
		_FresnelWeight("Night Rim Weight", Float) = 0
		_FresnelPower("Night Rim Power", Float) = 0
		_TestParams("Test Params", Vector) = (0,0,0,0)

		[Header(Atmosphere)]
		// 0 keeps the old white glint, 1 colours it by sun transmittance.
		// Exists for the before/after figure, not as an art dial.
		_GlintTransmittanceWeight("Glint Transmittance Weight", Range(0,1)) = 1
		// 0 reproduces the ocean before this work; 1 is the physical answer. For the ablation figure.
		_ReflectionStrength("Sky Reflection Strength", Range(0,2)) = 1
		// Water at n = 1.33: ((1.33-1)/(1.33+1))^2 = 0.02.
		_WaterF0("Water F0", Range(0,0.2)) = 0.02
		// How much of the wave normal perturbs the REFLECTION. Full perturbation is undersampled
		// noise at planet scale, and a rough surface reflects the average sky rather than a mirror.
		_ReflectionWaveWeight("Reflection Wave Weight", Range(0,1)) = 0.25
		// Skylight on the water body. The dial to reach for if the ocean is too dark or too flat.
		_SkyAmbientWeight("Sky Ambient Weight", Range(0,2)) = 0.1
		// Starlight and airglow. A sun-only atmosphere model has no night sky illumination at all,
		// so without this the ocean is mathematically black once the sun is down.
		_NightAmbient("Night Ambient", Color) = (0.012, 0.019, 0.035, 1)
		// Moonlight glint. Sharper than the sun s because the moon is a smaller disc.
		_MoonGlintSmoothness("Moon Glint Smoothness", Range(0.005, 0.3)) = 0.04
		// The old flat rim, now night-only and declared non-physical.
		_NightRimStrength("Night Rim Strength", Range(0,2)) = 1

		[Header(Foam)]
		[NoScaleOffset] _FoamDistanceMap ("Foam Distance Map", 2D) = "white" {}
		_FoamDst ("Foam Dst", Range(0,1)) = 1
		_FoamSpeed ("Foam Speed", Float) = 1
		_FoamFrequency ("Foam Frequency", Float) = 1
		_FoamWidth ("Foam Width", Float) = 1
		_FoamEdgeBlend ("Foam Edge Blend", Float) = 1
		_ShoreFoamDst ("Shore Foam Dst", Range(0, 1)) = 0.1
		_FoamNoiseSpeed ("Foam Noise Speed", Float) = 1
		_FoamNoiseStrength ("Foam Noise Strength", Float) = 1
		_FoamNoiseScale ("Foam Noise Scale", Float) = 1
		_FoamColour ("Foam Colour", Color) = (1,1,1,1)
		_FoamMaskScale ("Foam Mask Scale", Float) = 1
		_FoamMaskBlend ("Foam Mask Blend", Float) = 1
	}
	SubShader
	{
		Pass
		{
			Offset 1, 1 // In a Z-fight with the terrain, the ocean should lose (see https://docs.unity3d.com/Manual/SL-Offset.html)
			Tags { "LightMode" = "ForwardBase" "Queue" = "Geometry"}
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile_fwdbase

			#include "UnityCG.cginc"
			#include "UnityLightingCommon.cginc"
			#include "AutoLight.cginc"

			#include "Assets/Scripts/Shader Common/GeoMath.hlsl"
			#include "Assets/Scripts/Shader Common/Triplanar.hlsl"

			// Sun and moon colour after travelling through the air, and sky radiance for an
			// arbitrary direction. Also brings in TransmittanceCommon, SkyViewCommon and
			// DrawAtmosphereCommon, and declares TransmittanceLUT and skyReflectionStrength.
			//
			// Shared with Terrain.shader, which needs the same terms for the same reason: the land
			// has to warm at sunset alongside the water, or the coastline shows a seam.
			#include "Assets/Post Processing/Effects/Atmosphere/Shader Common/SurfaceLighting.hlsl"

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
				LIGHTING_COORDS(4,5)
			};

			
			float4 _TestParams;

			float4 _Tint;
			float _Saturation;
			sampler2D _OceanCol;
			float4 _OceanCol_TexelSize;

			float _SpecularSmoothness;
			float _WaveNormalScale, _WaveStrength, _WaveSpeed;
			sampler2D _WaveNormalA, _WaveNormalB, _Noise;

			float _Refraction;
			float _ShadowStrength;
			
			float4 _FresnelCol;
			float _FresnelWeight, _FresnelPower;

			float _GlintTransmittanceWeight;
			float _ReflectionStrength, _WaterF0, _ReflectionWaveWeight;
			float _SkyAmbientWeight, _NightRimStrength;
			float4 _NightAmbient;
			float _MoonGlintSmoothness;

			// Published by SolarSystem.Moon. Position, not direction: at 811 world units against a
			// 150-unit planet the moon is not far enough away to be directional.
			float4 moonPosition;
			float4 moonLightColour;   // already scaled by phase

			// Foam
			sampler2D _FoamDistanceMap;
			float _FoamSpeed;
			float _FoamFrequency;
			float _ShoreFoamDst;
			float _FoamWidth;
			float _FoamEdgeBlend;
			float _FoamDst;
			float _FoamNoiseSpeed;
			float _FoamNoiseScale;
			float _FoamNoiseStrength;
			float4 _FoamColour;
			float _FoamMaskScale;
			float _FoamMaskBlend;

			float3 calculateWaveNormals(float3 pos, float3 sphereNormal, out float3 tang) {
				float noise = triplanar(sphereNormal, sphereNormal, 0.15, _Noise).r;
	
				float waveSpeed = 0.35 * _WaveSpeed;
				float2 waveOffsetA = float2(_Time.x * waveSpeed, _Time.x * waveSpeed * 0.8);
				float2 waveOffsetB = float2(_Time.x * waveSpeed * - 0.8, _Time.x * waveSpeed * -0.5);

				float3 waveA = triplanarNormal(_WaveNormalA, pos, sphereNormal, _WaveNormalScale, waveOffsetA,_WaveStrength);
				float3 waveB = triplanarNormal(_WaveNormalA, pos, sphereNormal, _WaveNormalScale*0.9, waveOffsetA + float2(0.3,0.7),_WaveStrength);
				//float3 triplanarNormal(sampler2D normalMap, float3 pos, float3 normal, float3 scale, float2 offset, float normalScale, out float3 tangentNormal) {
				float3 waveNormal = triplanarNormal(_WaveNormalB, pos, lerp(waveA, waveB, noise), _WaveNormalScale * 1.25, waveOffsetB, _WaveStrength, tang);

				//return lerp(sphereNormal, waveNormal, _WaveStrength);
				return waveNormal;
			}

			// Calculate foam (rgb = colour; alpha = strength)
			float4 calculateFoam(float2 uv, float3 pointOnUnitSphere, float3 viewDir) {
				float dstFromShore = tex2D(_FoamDistanceMap, uv);
				dstFromShore = saturate(dstFromShore / _FoamDst);

				// Foam noise, used to make foam lines a bit jaggedy
				float2 noiseOffset = float2(0.0617, 0.0314) * _FoamNoiseSpeed * _Time.x;
				float foamNoise = triplanar(pointOnUnitSphere, pointOnUnitSphere, _FoamNoiseScale * 0.1, _Noise, noiseOffset).r;
				foamNoise = (foamNoise - 0.5) * _FoamNoiseStrength * dstFromShore; // increase noise strength further from the shore

				// More foam noise, this time used to fade out sections of the foam lines to break them up a bit
				float2 foamMaskOffset = float2(-0.021, 0.07) * _FoamNoiseSpeed * _Time.x;
				float foamMask = triplanar(pointOnUnitSphere, pointOnUnitSphere, _FoamMaskScale * 0.1, _Noise, foamMaskOffset).r;
				float threshold = lerp(0.375, 0.55, saturate(dstFromShore)); // mask out more further from the shore
				foamMask = smoothstep(threshold, threshold + _FoamMaskBlend * 0.01, foamMask);
				
				// Create foam lines radiating from shore using sin wave
				float foamStrength = sin(dstFromShore * _FoamFrequency - _Time.y * _FoamSpeed + foamNoise);
				foamStrength = saturate(smoothstep(_FoamWidth * 0.1 + _FoamEdgeBlend * 0.1, _FoamWidth * 0.1, foamStrength+1)) * foamMask;
				// Create constant line of foam at the shore
				float foamAtShore = smoothstep(_ShoreFoamDst + 0.1, _ShoreFoamDst, dstFromShore);
				foamStrength = saturate(foamStrength + foamAtShore);

				// Fade out foam as it gets further away
				foamStrength *= 1-smoothstep(0.7, 1, dstFromShore);

				// Fade based on view angle (to combat aliasing)
				float angleStrength = 1-smoothstep(-0.33 - 0.2, -0.33 + 0.2, dot(viewDir, pointOnUnitSphere));

				foamStrength = saturate(foamStrength * angleStrength);
				
				float3 foamColour = lerp(1, _FoamColour.rgb, dstFromShore);
				return float4(foamColour, foamStrength);
			}


			v2f vert (appdata v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.uv = v.uv;
				o.worldNormal = UnityObjectToWorldNormal(v.normal);
				o.worldPos =  mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1));

				TRANSFER_VERTEX_TO_FRAGMENT(o);
				return o;
			}

			// Parameter renamed from `dirToSun` to `sunDir`: AtmosphereCommon.hlsl declares a global
			// of the former name, and although this shader deliberately does not include it, a
			// shadowed uniform is the kind of thing that fails silently rather than loudly.
			float calculateSpecular(float3 normal, float3 viewDir, float3 sunDir, float smoothness) {
				float specularAngle = acos(dot(normalize(sunDir - viewDir), normal));
				float specularExponent = specularAngle / smoothness;
				float specularHighlight = exp(-max(0,specularExponent) * specularExponent);
				return specularHighlight;
			}


			float4 frag (v2f i) : SV_Target
			{
			
				float3 pointOnUnitSphere = normalize(i.worldPos);
				float3 sphereNormal = pointOnUnitSphere;

				float2 texCoord = pointToUV(pointOnUnitSphere);
				float mipLevel = calculateGeoMipLevel(texCoord, _OceanCol_TexelSize.zw);

				float shadows = LIGHT_ATTENUATION(i);
				float3 sunDir = _WorldSpaceLightPos0.xyz;
				float3 viewDir = normalize(i.worldPos - _WorldSpaceCameraPos.xyz);
				
				
				// ---- Calculate normals ----
				float3 tang;
				float3 waveNormal = calculateWaveNormals(i.worldPos, sphereNormal, tang);

				// ---- Get ocean colour ----
				float2 oceanRefractionTexCoord = texCoord + tang.xy * 0.0005 * _Refraction;
				float3 oceanCol = tex2Dlod(_OceanCol, float4(oceanRefractionTexCoord.xy, 0, mipLevel));

				// The water's base colour is BAKED into _OceanCol (Assets/Data/Ocean/Ocean.png) by
				// the offline generator in Assets/Scenes/Generators/Ocean Colour.unity - deep blue,
				// shallow blue and chlorophyll are all resolved there, per texel, against bathymetry.
				// So there is no single colour in this material that sets it, which is why _Tint
				// sitting here unread was actively misleading: picking a colour did nothing.
				//
				// These two are the live controls. Applied at the source so everything downstream -
				// shading, the Fresnel split, the sky ambient - inherits them.
				oceanCol = lerp(dot(oceanCol, float3(0.2126, 0.7152, 0.0722)), oceanCol, _Saturation);
				oceanCol *= _Tint.rgb;
	

				// ---- Calculate specular highlight---- 
				float3 specularNormal = waveNormal;
				float specularHighlight = saturate(calculateSpecular(specularNormal, viewDir, sunDir, _SpecularSmoothness));
				float specularStrength = lerp(0, 1, saturate(shadows * 5));
				specularStrength *= smoothstep(0.4f, 0.5, shadows);
				specularHighlight *= specularStrength;

				// # Apply shading and specular highlight
				float shading = dot(sphereNormal, sunDir) * 0.5 + 0.5;
				shading = shading * shading;
				float waveShading = dot(waveNormal, sunDir);
				//waveShading = max(0.5, waveShading);
				//return waveShading;
				//shading = lerp(shading, waveShading, 0.25);
				float grey = dot(oceanCol, float3(0.3, 0.3, 0.4));
				//return 1-grey;
				//shading += saturate(waveShading-0.75) * (1-grey) * 0.75;
				float waveShadeMask = lerp(0.4, 0.95, smoothstep(0.2, 1, dot(sphereNormal, sunDir)));
			//	return dot(waveNormal, viewDir);
				//shading += smoothstep(-0.1,0.5,dot(waveNormal, viewDir)) * 3;
				float ripple = saturate(smoothstep(-0.53,0.54,dot(waveNormal, viewDir)));
				//return ripple;
				oceanCol += ripple * 0.15;
				//shading += ripple *1;
				//oceanCol = oceanCol * lerp(ripple,1,_TestParams.z);


			//	//return waveShadeMask;
				//shading += saturate(waveShading-waveShadeMask) * (1-grey) * 1;
				//return saturate(waveShading-waveShadeMask) * (1-grey) * 1;
				// Colour the glint by the sun's own transmittance to this point, so it reddens and
				// dims through a sunset instead of staying white.
				//
				// `_LightColor0` alone is near-white here: Sun.cs estimates it from a gradient keyed
				// on dot(camera, sun), which is one colour for the whole globe and an approximation
				// its own comment flags. The transmittance LUT answers the same question per pixel
				// and physically.
				// Sampled at a canonical sea-level position rather than at i.worldPos, and gated on
				// the atmosphere actually being present. SurfaceLighting.hlsl owns both decisions and
				// explains why neither is optional; it falls back to white, so the glint comes out
				// uncoloured rather than black in the baseline modes.
				float3 sunTransmittance = sampleLightColour(sphereNormal, sunDir);
				// _LightColor0 is Sun.cs's gradient approximation, keyed on dot(camera, sun) - a single
				// colour for the whole globe. In physically based mode the transmittance lookup answers
				// the same question per pixel, and the terrain now uses it too, so keeping the gradient
				// as well would tint the glint twice and put the water out of step with the coast it
				// meets. The same gate fades it out, which leaves the baseline mode with exactly the
				// glint it was authored with - the gradient stays the baseline's path, not dead code.
				float3 glintCol = lerp(_LightColor0.rgb, 1, skyReflectionStrength)
					* lerp(1, sunTransmittance, _GlintTransmittanceWeight);

				// ---- Apply shadows ----
				// First, a little fix to the shadow value. When sun is on far side of planet, the far chunks of the earth
				// often don't get rendered for shadows due to culling distance. This means the ocean sometimes has chunks of
				// shadow missing. So crude fix is to just force the shadow value to zero (shadows on) when sufficiently dark.
				//
				// Moved above the compositing: `shadows` is sun visibility, so it belongs to the
				// water body and to the glint, and NOT to skylight arriving from some other
				// direction. Keeping it off the reflection is what makes the reflection darken at
				// night on its own, physically, rather than through the old flat rim hack.
				float nightT = saturate(dot(sphereNormal,-sunDir)); // 0 at sunrise/sunset to 1 at midnight
				shadows = lerp(shadows, 0, smoothstep(0.2,0.3,nightT));
				float sunVisibility = lerp(1, shadows, _ShadowStrength);

				// ---- The water body: direct sunlight plus skylight ----
				//
				// Skylight replaces the hand-painted `_Ambient`, which was a constant that had to be
				// re-authored for every world-scale preset and knew nothing about time of day. One
				// LUT tap toward the local zenith stands in for the hemispherical irradiance - an
				// approximation, since the hemisphere is really dominated by mid-elevations, but a
				// defensible one at one texture fetch.
				//
				// ADDITIVE, not multiplicative, and that is not the lazy choice - it is the correct
				// one for what this map actually is.
				//
				// Irradiance times albedo would be right if `_OceanCol` were an albedo. It is not:
				// it is a baked LIT-APPEARANCE map, with deep blue, shallow blue and chlorophyll
				// already resolved into a finished colour. Multiplying it by blue skylight squares
				// the blue - it took the map's blue-to-red ratio from 5:1 to 10:1, which reads as
				// an aggressively oversaturated ocean.
				//
				// Adding keeps the structure the flat `_Ambient` had, which looked right, and
				// replaces its hand-picked constant with the sky's own colour - so the water lifts
				// toward whatever the sky is doing instead of toward a fixed blue.
				//
				// Not attenuated by `sunVisibility`: that is the sun's shadow map, and skylight does
				// not arrive from the sun's direction.
				float3 skyAmbient = sampleSkyViewSafe(sphereNormal, sphereNormal, sunDir) * _SkyAmbientWeight;
				// Starlight and airglow, faded in as the sun goes down.
				//
				// The atmosphere model is sun-only: with the sun below the horizon the sky LUT is
				// genuinely zero, the shading term is zero, sunVisibility is forced to zero and the
				// glint's transmittance is exactly zero. Every source the ocean has goes to nothing
				// at once, which is why night was pure black rather than dark.
				//
				// Declared non-physical, like the night rim, and for the same reason: what it stands
				// for - integrated starlight and airglow - is simply outside a single-sun scattering
				// model rather than being something this renderer got wrong.
				float3 nightAmbient = _NightAmbient.rgb * smoothstep(0.0, 0.15, nightT);

				float3 bodyLit = saturate(saturate(oceanCol * shading) * sunVisibility + skyAmbient + oceanCol * nightAmbient);

				// ---- Sky reflection ----
				//
				// Only a fraction of the wave normal perturbs the mirror direction, damped further
				// at grazing angles. A screen pixel near the horizon covers an enormous number of
				// wave periods, so a fully perturbed reflection is undersampled noise rather than
				// detail - and the sky view LUT crowds its texels hardest exactly there, so it is
				// most sensitive to the perturbation in the same place the footprint is largest.
				// Physically a rough surface reflects the AVERAGE sky over its normal distribution,
				// which sits closer to the unperturbed direction than to a mirror.
				float grazing = 1 - saturate(dot(-viewDir, sphereNormal));
				float waveWeight = _ReflectionWaveWeight * (1 - grazing * grazing);
				float3 reflNormal = normalize(lerp(sphereNormal, waveNormal, waveWeight));

				// viewDir already points camera->surface, which is the incident vector reflect()
				// wants, so there is no negation here.
				float3 reflectDir = reflect(viewDir, reflNormal);

				// Lift the ray above the horizon. The LUT is baked at sea level, so everything
				// below the horizon in it is black by construction, and a perturbed normal can
				// easily throw the mirror direction down there - which would read as a dark fringe
				// hugging the horizon rather than as a reflection.
				reflectDir = normalize(reflectDir + sphereNormal * max(0, 0.002 - dot(reflectDir, sphereNormal)));

				// Schlick. F0 = 0.02 for water, so this is almost nothing looking down and
				// approaches 1 at grazing - which is exactly where the sunset sits.
				float cosIncidence = saturate(dot(-viewDir, reflNormal));
				float fresnelReflectance = _WaterF0 + (1 - _WaterF0) * pow(1 - cosIncidence, 5);
				fresnelReflectance = saturate(fresnelReflectance * _ReflectionStrength);

				// toneMap, because the sky was already tone-mapped into the colour buffer by the sky
				// pass - a raw radiance here would sit at a completely different exposure from the
				// sky one pixel above the horizon.
				float3 skyReflection = sampleSkyViewSafe(sphereNormal, reflectDir, sunDir);

				// lerp, never +=, for two independent reasons. It is the correct energy split - F
				// reflects and 1-F refracts into the body - so the sky REPLACES body colour rather
				// than adding to it, and at F -> 1 it reproduces the sky pixel exactly, which is
				// what makes the horizon line seamless. And toneMap(0) is about -0.05, because the
				// contrast pivot is a pedestal, so adding would SUBTRACT from the ocean at night.
				oceanCol = lerp(bodyLit, skyReflection, fresnelReflectance);

				// The glint ADDS to the sky reflection rather than lerping toward it.
				//
				// A lerp is wrong here as soon as the glint can be dark. sampleTransmittanceLUT
				// returns exactly 0 once the sun crosses the geometric horizon - correct, the sun
				// is occluded - but the specular lobe is a function of the half-vector and does not
				// know that, so it stays wide open. Lerping toward a black glint therefore punched
				// a dark hole in the middle of the bright orange sky reflection at exactly the
				// moment the sunset looked best.
				//
				// Adding is also the physically sensible reading: the sky view LUT deliberately
				// omits the sun's disc, so the glint is the missing part of the same reflection
				// rather than a competing one. When the sun sets it contributes nothing and the sky
				// reflection is left untouched.
				oceanCol += saturate(specularHighlight) * glintCol;

				// Moon glint, on exactly the same machinery as the sun's.
				//
				// The direction is computed per pixel from the moon's position rather than taken as
				// a constant, because at 811 units against a 150-unit planet it swings about ten
				// degrees across the visible globe - the scale a glint path is drawn at.
				//
				// The transmittance lookup does the horizon test for free: sampleTransmittanceLUT
				// returns exactly zero once the moon is below the local horizon, so the glint
				// appears and disappears with moonrise and moonset without a separate check, and
				// reddens near the horizon the same way the sun's does.
				float3 moonDir = normalize(moonPosition.xyz - i.worldPos);
				float3 moonTransmittance = sampleLightColour(sphereNormal, moonDir);
				float moonHighlight = saturate(calculateSpecular(waveNormal, viewDir, moonDir, _MoonGlintSmoothness));
				oceanCol += moonHighlight * moonLightColour.rgb * moonTransmittance;

				// # Apply foam
				float4 foam = calculateFoam(texCoord, pointOnUnitSphere, viewDir);
				oceanCol = lerp(oceanCol, foam.rgb * sunVisibility, foam.a);
				// Rim light, kept but gated on night.
				//
				// Its job - separating ocean from sky in silhouette - is done properly by the
				// physical Fresnel now, in daylight. At night it is not: the sky view LUT holds no
				// moonlight, no starlight and no airglow, so the reflection genuinely has nothing to
				// say and the horizon merges again. This stays as a declared non-physical term that
				// fades out the moment the physical one has anything to offer.
				float rim = saturate(_FresnelWeight * pow(1 + dot(viewDir, pointOnUnitSphere), _FresnelPower));
				oceanCol += rim * _FresnelCol.rgb * smoothstep(0.0, 0.25, nightT) * _NightRimStrength;

				// The camera buffer is 8-bit in gamma space and the reflection is a smooth gradient,
				// which is exactly what bands. The sky pass dithers at the same strength for the
				// same reason. SV_POSITION is in pixels in the fragment stage.
				oceanCol = blueNoiseDither(oceanCol, i.pos.xy / _ScreenParams.xy, ditherStrength);
				
				return float4(oceanCol, 1);
			}
			ENDCG
		}
	}
	Fallback "VertexLit"
}
