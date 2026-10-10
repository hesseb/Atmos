using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

[CreateAssetMenu( menuName = "PostProcessing/Atmosphere" )]
public class AtmosphereEffect : PostProcessingEffect
{
	public Shader drawSkyShader;
	public Light light;
	public float bodyRadius;
	public float atmosphereThickness;
	public int aerialStepsPerSlice = 6;

	//=============
	[Header( "Rayleigh Scattering" )]
	// Wavelengths of red, green, and blue light (nanometres)
	public Vector3 wavelengthsRGB = new Vector3( 700, 530, 460 );
	// Scale value to adjust all wavelengths at once
	public float wavelengthScale = 300;
	// Altitude [0, 1] at which the average density of particles causing rayleigh scattering is found
	[Range( 0, 1 )] public float rayleighDensityAvg = 0.1f;

	//=============
	[Header( "Mie Scattering" )]
	// Altitude [0, 1] at which the average density of particles causing mie scattering is found
	[Range( 0, 1 )] public float mieDensityAvg = 0.1f;
	// Strength of mie scattering
	public float mieCoefficient;
	// Strength of mie absorption
	public float mieAbsorption;
	// Asymmetry of the Cornette-Shanks phase function. 0 is isotropic, higher values scatter more sharply forward.
	[Range( 0, 0.99f )] public float mieAsymmetry = 0.8f;

	//=============
	[Header( "Density" )]
	// Multiplies every scattering/absorption coefficient: how much denser this atmosphere is than Earth's
	public float densityMultiplier = 1;

	//=============
	[Header( "Illumination" )]
	// Illuminance at the top of the atmosphere
	public float sunIlluminance = 4 * Mathf.PI;

	//=============
	[Header( "Ozone" )]
	//Altitude [0, 1] at which ozone density is at the greatest
	[Range( 0, 1 )] public float ozonePeakDensityAltitude = 0.25f;
	[Range( 0, 10 )] public float ozoneDensityFalloff = 4;
	[Range( 0, 5 )] public float ozoneStrength = 1;
	public Vector3 ozoneAbsorption;

	//=============
	[Header( "Sun Disc" )]
	public float sunDiscSize;
	public float sunDiscBlurA;
	public float sunDiscBlurB;

	//=============
	[Header( "Transmittance LUT (2D)" )]
	public ComputeShader transmittanceLUTCompute;
	public Vector2Int transmittanceLUTSize;

	//=============
	[Header( "Multiple Scattering LUT (2D)" )]
	public ComputeShader multipleScatteringLUTCompute;
	public Vector2Int multipleScatteringLUTSize = new Vector2Int( 32, 32 );
	[Range( 0, 2 )] public float multipleScatteringStrength = 1;
	[Range( 0, 1 )] public float groundAlbedo = 0.1f;

	//=============
	[Header( "Sky View LUT (2D)" )]
	public ComputeShader skyViewLUTCompute;
	public Vector2Int skyViewLUTSize = new Vector2Int( 192, 128 );
	public int numSkyViewScatteringSteps = 32;
	public float skyViewLutAltitude = 0f;
	[Range( 0.5f, 1f )] public float skyViewHorizonV = 1f;

	//=============
	[Header( "Aerial Perspective LUT" )]
	public ComputeShader aerialPerspectiveLUTCompute;
	public int aerialPerspectiveLUTSize;
	// Allow control over how strongly atmosphere affects appearance of terrain
	[Range( 0, 1 )] public float aerialPerspectiveStrength = 1;

	[Tooltip( "How much the cloud deck occludes multiply-scattered light, in addition to direct sun. " +
		"Without this, multiple scattering (a large share of in-scatter here) fills shafts back in " +
		"and washes them out. Partial, since some of that light genuinely arrives from the sides. " +
		"0 matches the previous behaviour." )]
	[Range( 0, 1 )] public float cloudShaftMultipleScattering = 0.7f;

	/// Draws the light-shaft occlusion term instead of the scene
	public enum DebugMode
	{
		Off = 0,
		ShaftVisibility = 1,
	}

	[Tooltip( "Shaft Visibility replaces the frame with the cloud occlusion the in-scatter march " +
		"sees, averaged along each view ray. White is unoccluded." )]
	public DebugMode debugMode = DebugMode.Off;

	//=============
	[Header( "Sky Texture" )]
	public int numSkyScatteringSteps = 100;
	public ComputeShader skyRenderCompute;
	// Note: since sky colours change quite smoothly this can be very small (e.g. 128x64)
	// However, the vertical resolution should be increased (~128x256) so that earth shadow isn't too jaggedy
	public Vector2Int skyRenderSize;
	// Allow control over how strongly atmosphere affects appearance of objects in the sky (moon, stars)
	[Range( 0, 1 )] public float skyTransmittanceWeight = 1;

	//=============
	[Header( "Tone mapping" )]
	public float intensity = 1;
	public float contrast = 1.45f;
	public float whitePoint = 1.1f;

	//=============
	[Header( "Dither" )]
	public float ditherStrength = 0.8f;
	public Texture2D blueNoise;

	//=============
	[Header( "Derived (read-only)" )]
	// Derived from wavelengthsRGB/wavelengthScale, shown for inspection only - editing it does
	// nothing, it is overwritten on the next settings update.
	public Vector3 rayleighCoefficients;

	//=============
	[Header( "LUT Textures (debug)" )]
	public RenderTexture transmittanceLUT;
	public RenderTexture multipleScatteringLUT;
	public RenderTexture skyViewLUT;
	public RenderTexture aerialLuminanceLUT;
	public RenderTexture aerialTransmittanceLUT;
	public RenderTexture skyLUT;
	bool settingsUpToDate;

	ShaderValues sharedAtmosphereValues;
	int settingsVersion;
	public event System.Action onSettingsUpdated;
	Material drawSkyMaterial;
	bool lutUpdateRequired;

	//=============

	public override void OnEnable()
	{
		base.OnEnable();
		settingsUpToDate = false;
		SetProperties();
		EditorOnlyInit();

		Camera.onPreCull -= RenderLUTs;
		Camera.onPreCull += RenderLUTs;
	}

	public void SetupSkyRenderingCommand( CommandBuffer skyRenderCommand )
	{
		lutUpdateRequired = true;

		if ( drawSkyMaterial == null || drawSkyMaterial.shader != drawSkyShader )
		{
			drawSkyMaterial = new Material( drawSkyShader ) { hideFlags = HideFlags.HideAndDontSave };
		}

		SkyPass.Record( skyRenderCommand, drawSkyMaterial );

		SetDrawSkyShaderParameters( drawSkyMaterial );
	}

	// Called on camera pre-cull
	void RenderLUTs( Camera activeCamera )
	{
		if ( lutUpdateRequired )
		{
			if ( activeCamera == cam )
			{
				lutUpdateRequired = false;
				FrustumCorners corners = CalculateFrustumCorners( activeCamera );

				RenderSky( corners );
				RenderSkyViewLUT( activeCamera );
				RenderAerialPerspectiveLUTs( activeCamera, corners );
			}
		}

		BindGlobalResources();
	}

	void BindGlobalResources()
	{
		sharedAtmosphereValues?.ApplyGlobal();

		Shader.SetGlobalFloat( "intensity", intensity );
		Shader.SetGlobalFloat( "contrast", contrast );
		Shader.SetGlobalFloat( "whitePoint", whitePoint );
		Shader.SetGlobalFloat( "ditherStrength", ditherStrength );

		if ( blueNoise != null ) { Shader.SetGlobalTexture( "BlueNoise", blueNoise ); }
		if ( transmittanceLUT != null ) { Shader.SetGlobalTexture( "TransmittanceLUT", transmittanceLUT ); }
		if ( multipleScatteringLUT != null ) { Shader.SetGlobalTexture( "MultipleScatteringLUT", multipleScatteringLUT ); }
		if ( skyViewLUT != null ) { Shader.SetGlobalTexture( "SkyViewLUT", skyViewLUT ); }
	}

	void OnDisable()
	{
		Camera.onPreCull -= RenderLUTs;
	}

	protected override void RenderEffectToTarget( RenderTexture source, RenderTexture target )
	{
		SetProperties();
		lutUpdateRequired = true;
		Graphics.Blit( source, target, material );
	}

	void ResolveSun()
	{
		if ( light != null || sunTagMissing ) { return; }

		GameObject sunObject;
		try
		{
			sunObject = GameObject.FindGameObjectWithTag( "Sun" );
		}
		catch ( UnityException )
		{
			sunTagMissing = true;
			Debug.LogWarning( "AtmosphereEffect: no \"Sun\" tag defined in this project, so the sun " +
				"direction cannot be resolved. Assign the light manually on the effect asset." );
			return;
		}

		light = sunObject != null ? sunObject.GetComponent<Light>() : null;
	}

	bool sunTagMissing;

	public void SetProperties()
	{
		ResolveSun();

		if ( material != null && light != null )
		{
			material.SetVector( ShaderParamID.dirToSun, -light.transform.forward );
		}

		BindComputeResources();

		if ( !settingsUpToDate || transmittanceLUT == null || multipleScatteringLUT == null )
		{
			sharedAtmosphereValues = GetShaderValues();
			sharedAtmosphereValues.Apply( material );
			if ( multipleScatteringLUTCompute != null ) { sharedAtmosphereValues.Apply( multipleScatteringLUTCompute ); }
			if ( skyViewLUTCompute != null ) { sharedAtmosphereValues.Apply( skyViewLUTCompute ); }
			sharedAtmosphereValues.Apply( transmittanceLUTCompute );
			sharedAtmosphereValues.Apply( aerialPerspectiveLUTCompute );
			sharedAtmosphereValues.Apply( skyRenderCompute );

			InitAndRenderTransmittanceLUT();
			InitAndRenderMultipleScatteringLUT();
			InitSkyViewLUT();
			InitAerialPerspectiveLUTs();
			InitSkyLUT();

			BindComputeResources();

			// Set shader params after all LUTs have been initialized
			SetDrawAerialPerspectiveShaderParams( material );

			// Draw sky settings
			if ( drawSkyMaterial != null )
			{
				SetDrawSkyShaderParameters( drawSkyMaterial );
			}

			// Done
			settingsUpToDate = true;

			settingsVersion++;
			Shader.SetGlobalFloat( "atmosphereSettingsVersion", settingsVersion );

			onSettingsUpdated?.Invoke();
		}
	}

	public void ApplyAtmosphereValuesTo( ComputeShader compute )
	{
		SetProperties();
		GetShaderValues().Apply( compute );

		if ( multipleScatteringLUT != null ) { compute.SetTexture( 0, "MultipleScatteringLUT", multipleScatteringLUT ); }
	}

	ShaderValues GetShaderValues()
	{
		ShaderValues values = new ShaderValues();
		// Size values
		values.floats.Add( ("atmosphereThickness", atmosphereThickness) );
		values.floats.Add( ("atmosphereRadius", bodyRadius + atmosphereThickness) );
		values.floats.Add( ("planetRadius", bodyRadius) );

		values.vectors.Add( ("transmittanceLutSize", new Vector2( transmittanceLUTSize.x, transmittanceLUTSize.y )) );

		float thickness = atmosphereThickness;

		// Rayleigh values
		values.floats.Add( ("rayleighScaleHeight", Mathf.Max( 1e-4f, rayleighDensityAvg * thickness )) );
		// Arbitrary scale to give nicer range of reasonable values for the scattering constant
		// Strength of (rayleigh) scattering is dependent on wavelength (~ 1/wavelength^4)
		Vector3 inverseWavelengths = new Vector3( 1 / wavelengthsRGB.x, 1 / wavelengthsRGB.y, 1 / wavelengthsRGB.z );
		Vector3 rayleigh = Pow( inverseWavelengths * wavelengthScale, 4 );
		values.vectors.Add( ("rayleighCoefficients", rayleigh * densityMultiplier / thickness) );

		// Mirrored only when changed - writing unconditionally dirtied the asset on every update.
		if ( rayleighCoefficients != rayleigh ) { rayleighCoefficients = rayleigh; }

		// Mie values
		values.floats.Add( ("mieScaleHeight", Mathf.Max( 1e-4f, mieDensityAvg * thickness )) );
		values.floats.Add( ("mieCoefficient", mieCoefficient * densityMultiplier / thickness) );
		values.floats.Add( ("mieAbsorption", mieAbsorption * densityMultiplier / thickness) );
		values.floats.Add( ("mieAsymmetry", mieAsymmetry) );

		values.floats.Add( ("sunIlluminance", sunIlluminance) );

		values.vectors.Add( ("multipleScatteringLutSize", new Vector2( multipleScatteringLUTSize.x, multipleScatteringLUTSize.y )) );
		values.floats.Add( ("multipleScatteringStrength", multipleScatteringStrength) );
		values.floats.Add( ("cloudShaftMultipleScattering", cloudShaftMultipleScattering) );
		values.floats.Add( ("groundAlbedo", groundAlbedo) );

		values.vectors.Add( ("skyViewLutSize", new Vector2( skyViewLUTSize.x, skyViewLUTSize.y )) );
		values.floats.Add( ("skyViewLutRadius", bodyRadius + skyViewLutAltitude) );
		values.floats.Add( ("skyViewHorizonV", skyViewHorizonV) );

		values.floats.Add( ("ozonePeakAltitude", ozonePeakDensityAltitude * thickness) );
		values.floats.Add( ("ozoneHalfWidth", thickness / Mathf.Max( 1e-4f, ozoneDensityFalloff )) );
		values.vectors.Add( ("ozoneAbsorption", ozoneAbsorption * ozoneStrength * densityMultiplier * 0.1f / thickness) );

		return values;
	}

	void SetDrawSkyShaderParameters( Material drawSky )
	{
		// Textures
		drawSky.SetTexture( "SkyLUT", skyLUT );
		drawSky.SetTexture( "TransmittanceLUT", transmittanceLUT );
		drawSky.SetTexture( "BlueNoise", blueNoise );

		// Values
		drawSky.SetFloat( "atmosphereThickness", atmosphereThickness );
		drawSky.SetFloat( "planetRadius", bodyRadius );
		drawSky.SetFloat( "atmosphereRadius", bodyRadius + atmosphereThickness );
		drawSky.SetVector( "transmittanceLutSize", new Vector2( transmittanceLUTSize.x, transmittanceLUTSize.y ) );
		drawSky.SetFloat( "sunDiscSize", sunDiscSize );
		drawSky.SetFloat( "sunDiscBlurA", sunDiscBlurA );
		drawSky.SetFloat( "sunDiscBlurB", sunDiscBlurB );
		drawSky.SetFloat( "ditherStrength", ditherStrength );
		drawSky.SetFloat( "intensity", intensity );
		drawSky.SetFloat( "contrast", contrast );
		drawSky.SetFloat( "whitePoint", whitePoint );
		drawSky.SetFloat( "skyTransmittanceWeight", skyTransmittanceWeight );
	}


	void SetDrawAerialPerspectiveShaderParams( Material drawAerial )
	{
		// Textures
		drawAerial.SetTexture( "AerialLuminanceLUT", aerialLuminanceLUT );
		drawAerial.SetTexture( "AerialTransmittanceLUT", aerialTransmittanceLUT );
		drawAerial.SetTexture( "BlueNoise", blueNoise );

		// Values
		drawAerial.SetFloat( "ditherStrength", ditherStrength );
		drawAerial.SetFloat( "aerialPerspectiveStrength", aerialPerspectiveStrength );
		drawAerial.SetInt( "atmosphereDebugMode", (int)debugMode );

		drawAerial.SetFloat( "intensity", intensity );
		drawAerial.SetFloat( "contrast", contrast );
		drawAerial.SetFloat( "whitePoint", whitePoint );
	}

	// Create lookup texture for the transmittance (proportion of light reaching given point through the atmosphere)
	// This only needs to be created once at the start (or whenever atmosphere parameters are changed)
	void InitAndRenderTransmittanceLUT()
	{
		GraphicsFormat transmittanceLUTFormat = GraphicsFormat.R16G16B16A16_UNorm;
		ComputeHelper.CreateRenderTexture( ref transmittanceLUT, transmittanceLUTSize.x, transmittanceLUTSize.y, FilterMode.Bilinear, transmittanceLUTFormat, "Transmittance LUT" );
		transmittanceLUTCompute.SetTexture( 0, "TransmittanceLUT", transmittanceLUT );
		transmittanceLUTCompute.SetInt( "width", transmittanceLUTSize.x );
		transmittanceLUTCompute.SetInt( "height", transmittanceLUTSize.y );
		ComputeHelper.Dispatch( transmittanceLUTCompute, transmittanceLUT );
	}

	void InitAndRenderMultipleScatteringLUT()
	{
		if ( multipleScatteringLUTCompute == null ) { return; }

		ComputeHelper.CreateRenderTexture( ref multipleScatteringLUT, multipleScatteringLUTSize.x, multipleScatteringLUTSize.y,
			FilterMode.Bilinear, GraphicsFormat.R16G16B16A16_SFloat, "Multiple Scattering LUT" );

		multipleScatteringLUTCompute.SetTexture( 0, "MultipleScatteringOutputLUT", multipleScatteringLUT );
		multipleScatteringLUTCompute.SetTexture( 0, "TransmittanceLUT", transmittanceLUT );
		ComputeHelper.Dispatch( multipleScatteringLUTCompute, multipleScatteringLUT );
	}

	void InitSkyViewLUT()
	{
		if ( skyViewLUTCompute == null ) { return; }

		ComputeHelper.CreateRenderTexture( ref skyViewLUT, skyViewLUTSize.x, skyViewLUTSize.y,
			FilterMode.Bilinear, GraphicsFormat.R16G16B16A16_SFloat, "Sky View LUT" );
	}

	void InitAerialPerspectiveLUTs()
	{
		GraphicsFormat aerialPerspectiveLUTFormat = GraphicsFormat.R16G16B16A16_SFloat;
		GraphicsFormat aerialTransmittanceFormat = GraphicsFormat.R16G16B16A16_UNorm;
		ComputeHelper.CreateRenderTexture3D( ref aerialLuminanceLUT, aerialPerspectiveLUTSize, aerialPerspectiveLUTFormat, TextureWrapMode.Clamp, "Aerial Luminance LUT" );
		ComputeHelper.CreateRenderTexture3D( ref aerialTransmittanceLUT, aerialPerspectiveLUTSize, aerialTransmittanceFormat, TextureWrapMode.Clamp, "Aerial Transmittance LUT" );
	}

	void RenderAerialPerspectiveLUTs( Camera cam, FrustumCorners corners )
	{
		if ( aerialPerspectiveLUTCompute == null || light == null ) { return; }

		// Assign dynamic values
		SetFrustumCorners( aerialPerspectiveLUTCompute, corners );
		aerialPerspectiveLUTCompute.SetVector( ShaderParamID.dirToSun, -light.transform.forward );

		ComputeHelper.Dispatch( aerialPerspectiveLUTCompute, aerialPerspectiveLUTSize, aerialPerspectiveLUTSize, 1 );
	}

	void InitSkyLUT()
	{
		GraphicsFormat skyFormat = GraphicsFormat.R16G16B16A16_SFloat;
		ComputeHelper.CreateRenderTexture( ref skyLUT, skyRenderSize.x, skyRenderSize.y, FilterMode.Bilinear, skyFormat, "Sky LUT" );
	}

	void BindComputeResources()
	{
		if ( multipleScatteringLUT != null )
		{
			if ( material != null ) { material.SetTexture( "MultipleScatteringLUT", multipleScatteringLUT ); }
			if ( skyRenderCompute != null ) { skyRenderCompute.SetTexture( 0, "MultipleScatteringLUT", multipleScatteringLUT ); }
			if ( aerialPerspectiveLUTCompute != null ) { aerialPerspectiveLUTCompute.SetTexture( 0, "MultipleScatteringLUT", multipleScatteringLUT ); }
		}

		CloudShadowGlobals.BindTo( skyViewLUTCompute, 0 );
		CloudShadowGlobals.BindTo( skyRenderCompute, 0 );
		CloudShadowGlobals.BindTo( aerialPerspectiveLUTCompute, 0 );
		CloudShadowGlobals.BindTo( multipleScatteringLUTCompute, 0 );

		if ( skyViewLUTCompute != null && skyViewLUT != null )
		{
			skyViewLUTCompute.SetTexture( 0, "SkyViewOutputLUT", skyViewLUT );
			skyViewLUTCompute.SetTexture( 0, "TransmittanceLUT", transmittanceLUT );
			if ( multipleScatteringLUT != null ) { skyViewLUTCompute.SetTexture( 0, "MultipleScatteringLUT", multipleScatteringLUT ); }
			skyViewLUTCompute.SetInt( "numSkyViewSteps", numSkyViewScatteringSteps );
		}

		if ( skyRenderCompute != null && skyLUT != null )
		{
			skyRenderCompute.SetTexture( 0, "TransmittanceLUT", transmittanceLUT );
			skyRenderCompute.SetTexture( 0, "SkyLUT", skyLUT );
			skyRenderCompute.SetInt( "numScatteringSteps", numSkyScatteringSteps );
			skyRenderCompute.SetInts( "size", skyRenderSize.x, skyRenderSize.y );
		}

		if ( aerialPerspectiveLUTCompute != null && aerialLuminanceLUT != null )
		{
			aerialPerspectiveLUTCompute.SetTexture( 0, "AerialLuminanceLUT", aerialLuminanceLUT );
			aerialPerspectiveLUTCompute.SetTexture( 0, "AerialTransmittanceLUT", aerialTransmittanceLUT );
			aerialPerspectiveLUTCompute.SetTexture( 0, "TransmittanceLUT", transmittanceLUT );
			aerialPerspectiveLUTCompute.SetInt( "size", aerialPerspectiveLUTSize );
			aerialPerspectiveLUTCompute.SetInt( "stepsPerSlice", aerialStepsPerSlice );
		}
	}

	// Render the sky to a small texture, which then will be upscaled to reduce expensive raymarching
	// This is rendered every frame
	void RenderSky( FrustumCorners corners )
	{
		if ( skyRenderCompute == null || light == null ) { return; }

		SetFrustumCorners( skyRenderCompute, corners );
		skyRenderCompute.SetVector( ShaderParamID.dirToSun, -light.transform.forward );
		ComputeHelper.Dispatch( skyRenderCompute, skyLUT );
	}

	void RenderSkyViewLUT( Camera cam )
	{
		if ( skyViewLUTCompute == null || skyViewLUT == null || light == null ) { return; }

		Vector3 up = cam.transform.position.normalized;
		float cosSunZenith = Mathf.Clamp( Vector3.Dot( up, -light.transform.forward ), -1f, 1f );
		float sinSunZenith = Mathf.Sqrt( Mathf.Max( 0f, 1f - cosSunZenith * cosSunZenith ) );

		skyViewLUTCompute.SetVector( ShaderParamID.dirToSun, new Vector3( sinSunZenith, cosSunZenith, 0f ) );
		ComputeHelper.Dispatch( skyViewLUTCompute, skyViewLUT );
	}

	struct FrustumCorners
	{
		public Vector3 topLeft, topRight, bottomLeft, bottomRight;
		public Vector3 camPos;
	}

	static FrustumCorners CalculateFrustumCorners( Camera camera )
	{
		Matrix4x4 clipToView = GL.GetGPUProjectionMatrix( camera.projectionMatrix, true ).inverse;
		Matrix4x4 viewToWorld = camera.transform.localToWorldMatrix;

		FrustumCorners corners;
		corners.topLeft = CornerDirection( clipToView, viewToWorld, 0, 1 );
		corners.topRight = CornerDirection( clipToView, viewToWorld, 1, 1 );
		corners.bottomLeft = CornerDirection( clipToView, viewToWorld, 0, 0 );
		corners.bottomRight = CornerDirection( clipToView, viewToWorld, 1, 0 );
		corners.camPos = camera.transform.position;
		return corners;
	}

	static Vector3 CornerDirection( Matrix4x4 clipToView, Matrix4x4 viewToWorld, float u, float v )
	{
		Vector3 viewVector = clipToView * new Vector4( u * 2 - 1, v * 2 - 1, 0, -1 );
		viewVector = viewToWorld * new Vector4( viewVector.x, viewVector.y, viewVector.z, 0 );
		return viewVector.normalized;
	}

	static void SetFrustumCorners( ComputeShader compute, FrustumCorners corners )
	{
		compute.SetVector( ShaderParamID.topLeftDir, corners.topLeft );
		compute.SetVector( ShaderParamID.topRightDir, corners.topRight );
		compute.SetVector( ShaderParamID.bottomLeftDir, corners.bottomLeft );
		compute.SetVector( ShaderParamID.bottomRightDir, corners.bottomRight );
		compute.SetVector( ShaderParamID.camPos, corners.camPos );
	}

	public static class ShaderParamID
	{
		public static int topLeftDir = Shader.PropertyToID( "topLeftDir" );
		public static int topRightDir = Shader.PropertyToID( "topRightDir" );
		public static int bottomLeftDir = Shader.PropertyToID( "bottomLeftDir" );
		public static int bottomRightDir = Shader.PropertyToID( "bottomRightDir" );
		public static int camPos = Shader.PropertyToID( "camPos" );

		public static int dirToSun = Shader.PropertyToID( "dirToSun" );

	}

	public override void OnDestroy()
	{
		ComputeHelper.Release( aerialLuminanceLUT, skyLUT, transmittanceLUT, aerialTransmittanceLUT,
			multipleScatteringLUT, skyViewLUT );
	}

	public void MarkSettingsDirty() => settingsUpToDate = false;

	void OnValidate()
	{
		if ( Application.isEditor )
		{
			settingsUpToDate = false;
		}
	}

	Vector3 Pow( Vector3 vector, float power )
	{
		return new Vector3( Mathf.Pow( vector.x, power ), Mathf.Pow( vector.y, power ), Mathf.Pow( vector.z, power ) );
	}

	void EditorOnlyInit()
	{
#if UNITY_EDITOR
		EditorShaderHelper.onRebindRequired += () => settingsUpToDate = false;
#endif
	}

	public class ShaderValues
	{
		public List<(string name, float value)> floats;
		public List<(string name, Vector4 value)> vectors;

		public ShaderValues()
		{
			floats = new List<(string name, float value)>();
			vectors = new List<(string name, Vector4 value)>();
		}

		public void Apply( Material material )
		{
			foreach ( var data in floats )
			{
				material.SetFloat( data.name, data.value );
			}

			foreach ( var data in vectors )
			{
				material.SetVector( data.name, data.value );
			}
		}

		public void Apply( ComputeShader compute )
		{
			foreach ( var data in floats )
			{
				compute.SetFloat( data.name, data.value );
			}

			foreach ( var data in vectors )
			{
				compute.SetVector( data.name, data.value );
			}
		}

		public void ApplyGlobal()
		{
			foreach ( var data in floats )
			{
				Shader.SetGlobalFloat( data.name, data.value );
			}

			foreach ( var data in vectors )
			{
				Shader.SetGlobalVector( data.name, data.value );
			}
		}


	}
}
