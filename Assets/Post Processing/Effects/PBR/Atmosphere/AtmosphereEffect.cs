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


	/// <summary>
	/// Raymarch steps per depth slice of the aerial perspective LUT (total cost is size x this,
	/// since the march is incremental per slice).
	/// </summary>
	public int aerialStepsPerSlice = 6;


	[Header( "Rayleigh Scattering" )]
	// Wavelengths of red, green, and blue light (nanometres)
	public Vector3 wavelengthsRGB = new Vector3( 700, 530, 460 );
	// Scale value to adjust all wavelengths at once
	public float wavelengthScale = 300;
	// Altitude [0, 1] at which the average density of particles causing rayleigh scattering is found
	[Range( 0, 1 )] public float rayleighDensityAvg = 0.1f;

	[Header( "Mie Scattering" )]
	// Altitude [0, 1] at which the average density of particles causing mie scattering is found
	[Range( 0, 1 )] public float mieDensityAvg = 0.1f;
	// Strength of mie scattering
	public float mieCoefficient;
	// Strength of mie absorption
	public float mieAbsorption;
	/// <summary>
	/// Asymmetry of the Cornette-Shanks phase function. 0 is isotropic (reduces to the Rayleigh
	/// phase); higher values scatter more sharply forward.
	/// </summary>
	[Range( 0, 0.99f )] public float mieAsymmetry = 0.8f;

	[Header( "Density" )]
	/// <summary>
	/// Multiplies every scattering/absorption coefficient - how much denser this atmosphere is
	/// than Earth's. Needed because the small planet's slant path is too short to redden a sunset
	/// at Earth's coefficients; density is the one named compensating factor, rather than several
	/// constants quietly bent away from their published values. Tuned so the horizon optical
	/// depth in blue matches Earth's (see the validation harness).
	/// </summary>
	public float densityMultiplier = 1;

	[Header( "Illumination" )]
	/// <summary>
	/// Illuminance at the top of the atmosphere. 4*PI matches the average in-scatter the old
	/// hardcoded `rayleighPhaseValue = 1` produced; not yet tied to SI radiometry.
	/// </summary>
	public float sunIlluminance = 4 * Mathf.PI;

	[Header( "Ozone" )]
	//Altitude [0, 1] at which ozone density is at the greatest
	[Range( 0, 1 )] public float ozonePeakDensityAltitude = 0.25f;
	[Range( 0, 10 )] public float ozoneDensityFalloff = 4;
	[Range( 0, 5 )] public float ozoneStrength = 1;
	public Vector3 ozoneAbsorption;

	[Header( "Sun Disc" )]
	public float sunDiscSize;
	public float sunDiscBlurA;
	public float sunDiscBlurB;

	[Header( "Transmittance LUT (2D)" )]
	public ComputeShader transmittanceLUTCompute;
	public Vector2Int transmittanceLUTSize;

	[Header( "Multiple Scattering LUT (2D)" )]
	public ComputeShader multipleScatteringLUTCompute;
	/// <summary>32x32 is Hillaire's size and is ample: Psi_ms is smooth in both altitude and sun elevation.</summary>
	public Vector2Int multipleScatteringLUTSize = new Vector2Int( 32, 32 );
	/// <summary>0 disables multiple scattering, leaving single scattering alone - the control condition for the comparison.</summary>
	[Range( 0, 2 )] public float multipleScatteringStrength = 1;
	/// <summary>Ground reflectance for the LUT's Lambertian bounce. 0.1 is Hillaire's default, about right for ocean-and-land average.</summary>
	[Range( 0, 1 )] public float groundAlbedo = 0.1f;

	[Header( "Sky View LUT (2D)" )]
	public ComputeShader skyViewLUTCompute;
	/// <summary>
	/// 192 x 128 is Hillaire's size. The parameterisation crowds v toward the horizon, so most of
	/// that height is spent on the band that actually varies.
	/// </summary>
	public Vector2Int skyViewLUTSize = new Vector2Int( 192, 128 );
	public int numSkyViewScatteringSteps = 32;
	/// <summary>
	/// Altitude the LUT is evaluated at. 0 (sea level) is correct for the ocean's reflection: the
	/// Mie scale height is tiny next to camera altitude, so a camera-altitude LUT would give the
	/// ocean a clean sky where a warm horizon glow belongs; sea level also avoids double-counting
	/// the camera-to-surface segment the aerial perspective already applies over the ocean pixel.
	/// </summary>
	public float skyViewLutAltitude = 0f;
	/// <summary>
	/// Where the horizon sits in v. 1 stores only the upper hemisphere, since a sea-level LUT's
	/// lower half is definitionally black; 0.5 recovers Hillaire's full-sphere split for a future
	/// consumer at altitude.
	/// </summary>
	[Range( 0.5f, 1f )] public float skyViewHorizonV = 1f;

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

	/// <summary>
	/// Draws the light-shaft occlusion term instead of the scene, to tell "no shafts" apart from
	/// "shafts too faint to see" - different causes, different fixes. White means
	/// cloudSunVisibility is returning 1 (a plumbing/geometry fault); cloud-shaped darkening means
	/// the term works and it's a tuning issue.
	/// </summary>
	public enum DebugMode
	{
		Off = 0,
		ShaftVisibility = 1,
	}

	[Tooltip( "Shaft Visibility replaces the frame with the cloud occlusion the in-scatter march " +
		"sees, averaged along each view ray. White is unoccluded." )]
	public DebugMode debugMode = DebugMode.Off;


	[Header( "Sky Texture" )]
	// Num raymarch steps when drawing the sky (this is drawn small and upscaled, so can afford to be fairly high)
	public int numSkyScatteringSteps = 100;
	public ComputeShader skyRenderCompute;
	// Note: since sky colours change quite smoothly this can be very small (e.g. 128x64)
	// However, the vertical resolution should be increased (~128x256) so that earth shadow isn't too jaggedy
	public Vector2Int skyRenderSize;
	// Allow control over how strongly atmosphere affects appearance of objects in the sky (moon, stars)
	[Range( 0, 1 )] public float skyTransmittanceWeight = 1;

	[Header( "Tone mapping" )]
	public float intensity = 1;
	public float contrast = 1.45f;
	public float whitePoint = 1.1f;


	[Header( "Later" )]
	public float ditherStrength = 0.8f;
	public Texture2D blueNoise;

	[Header( "Debug" )]
	/// <summary>
	/// Derived from wavelengthsRGB/wavelengthScale, shown for inspection only - editing it does
	/// nothing, it is overwritten on the next settings update.
	/// </summary>
	public Vector3 rayleighCoefficients;

	[Header( ( "Debug" ) )]
	public RenderTexture transmittanceLUT;
	public RenderTexture multipleScatteringLUT;
	public RenderTexture skyViewLUT;
	public RenderTexture aerialPerspectiveLuminance;
	public RenderTexture aerialPerspectiveTransmittance;
	public RenderTexture sky;
	bool settingsUpToDate;

	ShaderValues sharedAtmosphereValues;
	int settingsVersion;
	public event System.Action onSettingsUpdated;
	Material drawSkyMaterial;
	bool lutUpdateRequired;

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

		// HideAndDontSave: recreated every domain reload under [ExecuteInEditMode], otherwise
		// each reload leaks a material.
		if ( drawSkyMaterial == null || drawSkyMaterial.shader != drawSkyShader )
		{
			drawSkyMaterial = new Material( drawSkyShader ) { hideFlags = HideFlags.HideAndDontSave };
		}

		// Shared with the baseline sky, so the two passes cannot drift apart.
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

				// Derived once here rather than inside each consumer: both marches parameterise
				// their rays off the same four corners, and deriving them costs a matrix inversion.
				FrustumCorners corners = CalculateFrustumCorners( activeCamera );

				RenderSky( corners );
				RenderSkyViewLUT( activeCamera );
				RenderAerialPerspectiveLUTs( activeCamera, corners );
			}
		}

		// Outside the dirty-flag branch and outside SetProperties on purpose: this runs on
		// Camera.onPreCull, before opaque geometry, while SetProperties runs from OnRenderImage
		// after the ocean has already shaded - binding there would leave scene shaders reading
		// last frame's values, or none at all on the first frame.
		BindGlobalResources();
	}

	/// <summary>
	/// Publishes the atmosphere's values and LUTs as globals, for scene shaders this effect
	/// doesn't own a reference to (the ocean's sun-transmittance glint is the first consumer).
	/// See ShaderValues.ApplyGlobal for why globals rather than a material reference.
	/// </summary>
	void BindGlobalResources()
	{
		sharedAtmosphereValues?.ApplyGlobal();

		// The tone map, so scene shaders can put sky radiance into the colour buffer at the same
		// exposure the sky pass used. Not part of ShaderValues (which only the atmosphere's own
		// materials get) since these change with the world-scale preset and can't be authored
		// directly on the ocean material.
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
		// This effect owns no command buffer - RenderingManager creates and owns the sky buffer's
		// lifetime. The RemoveAllCommandBuffers() that used to be here was collateral damage: it
		// also detached the stars and moon on domain reload, and nothing then reattached them.
		Camera.onPreCull -= RenderLUTs;
	}

	protected override void RenderEffectToTarget( RenderTexture source, RenderTexture target )
	{
		SetProperties();
		lutUpdateRequired = true;
		Graphics.Blit( source, target, material );
	}

	/// <summary>
	/// Finds the Sun once, and stops asking if the project has no "Sun" tag. FindGameObjectWithTag
	/// throws rather than returning null for an undefined tag, so this guards against an exception
	/// per frame. The retry stays because OnEnable can run before the scene's Sun exists.
	/// </summary>
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
		// Was `!settingsUpToDate || !Application.isPlaying`, which reran the whole init branch
		// (including the transmittance LUT dispatch) every frame in edit mode. The dirty flag
		// alone suffices: OnValidate sets it on inspector edits, OnEnable clears it on load, and
		// EditorShaderHelper sets it when focus regain drops compute bindings. The null check
		// catches a lost render texture, the one case that wouldn't otherwise re-trigger.
		// Bindings are rebound unconditionally since they're cheap and don't survive a domain
		// reload; only LUT creation and dispatch stay behind the dirty flag.
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
			InitAeiralPerspectiveLUTs();
			InitSkyLUT();

			// Rebind now that the multiple-scattering LUT exists (BindComputeResources ran before
			// this branch, when it was still null). This is the only rebind the Init calls need -
			// they used to each end with their own, giving five rebind passes where two suffice.
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

			// Stamp of the settings the LUTs were built from, published as a global the same
			// decoupled way RenderingManager publishes skyReflectionStrength. The cloud deck's
			// radiance bake reads this to notice when the baked LUTs' contents change.
			settingsVersion++;
			Shader.SetGlobalFloat( "atmosphereSettingsVersion", settingsVersion );

			onSettingsUpdated?.Invoke();
		}
	}

	/// <summary>
	/// Applies the atmosphere parameters to an arbitrary compute shader, after ensuring its LUTs
	/// are current. Used by the baseline renderer's offline sky bake, so it evaluates the same
	/// scattering code as the runtime renderer rather than a reimplementation that could silently
	/// diverge from it.
	/// </summary>
	public void ApplyAtmosphereValuesTo( ComputeShader compute )
	{
		// SetProperties takes its init branch whenever settings are dirty or a LUT is missing, so
		// after this call the transmittance LUT is guaranteed current.
		SetProperties();
		GetShaderValues().Apply( compute );

		// The bakers call raymarch() too, so they need the multiple-scattering LUT bound like
		// every other consumer - otherwise the bake would omit a term the runtime has, measuring
		// the bake rather than the technique.
		if ( multipleScatteringLUT != null ) { compute.SetTexture( 0, "MultipleScatteringLUT", multipleScatteringLUT ); }
	}

	ShaderValues GetShaderValues()
	{
		ShaderValues values = new ShaderValues();
		// Size values
		values.floats.Add( ("atmosphereThickness", atmosphereThickness) );
		values.floats.Add( ("atmosphereRadius", bodyRadius + atmosphereThickness) );
		values.floats.Add( ("planetRadius", bodyRadius) );

		// Divided by on both write and read, so this has to be one shared value rather than two
		// int uniforms that could drift apart.
		values.vectors.Add( ("transmittanceLutSize", new Vector2( transmittanceLUTSize.x, transmittanceLUTSize.y )) );

		// The shader works in absolute altitude and per-world-unit coefficients; the inspector's
		// normalised fields are converted here so the physics stays numerically checkable.
		float thickness = atmosphereThickness;

		// Rayleigh values
		// Floored like ozoneHalfWidth below: both scale heights are inspector fractions that can
		// hit exactly zero, and getScatteringValues divides by them inside exp() - producing a NaN
		// that survives every downstream saturate/clamp rather than a harmless infinity.
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

		// Dimensionless, so no thickness conversion - it only affects the phase, not optical
		// depth/transmittance.
		values.floats.Add( ("sunIlluminance", sunIlluminance) );

		// Sent as float2 since both write and read divide by it - an int pair could let two
		// conversions disagree.
		values.vectors.Add( ("multipleScatteringLutSize", new Vector2( multipleScatteringLUTSize.x, multipleScatteringLUTSize.y )) );
		values.floats.Add( ("multipleScatteringStrength", multipleScatteringStrength) );
		values.floats.Add( ("cloudShaftMultipleScattering", cloudShaftMultipleScattering) );
		values.floats.Add( ("groundAlbedo", groundAlbedo) );

		// Sky view. The size goes as a float2 because both the write and the read divide by it.
		values.vectors.Add( ("skyViewLutSize", new Vector2( skyViewLUTSize.x, skyViewLUTSize.y )) );
		values.floats.Add( ("skyViewLutRadius", bodyRadius + skyViewLutAltitude) );
		values.floats.Add( ("skyViewHorizonV", skyViewHorizonV) );

		// Ozone values. The tent was `1 - |peak01 - h01| * falloff` in normalised altitude, so
		// its half-width in world units is thickness / falloff.
		values.floats.Add( ("ozonePeakAltitude", ozonePeakDensityAltitude * thickness) );
		values.floats.Add( ("ozoneHalfWidth", thickness / Mathf.Max( 1e-4f, ozoneDensityFalloff )) );
		values.vectors.Add( ("ozoneAbsorption", ozoneAbsorption * ozoneStrength * densityMultiplier * 0.1f / thickness) );

		return values;
	}

	void SetDrawSkyShaderParameters( Material drawSky )
	{
		// Textures
		drawSky.SetTexture( "Sky", sky );
		drawSky.SetTexture( "TransmittanceLUT", transmittanceLUT );
		drawSky.SetTexture( "BlueNoise", blueNoise );

		// Values
		drawSky.SetFloat( "atmosphereThickness", atmosphereThickness );
		drawSky.SetFloat( "planetRadius", bodyRadius );
		// Bruneton's parameterisation needs both radius and size - a silent zero here would
		// divide the whole mapping by nothing and put every sample in one corner of the LUT.
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
		drawAerial.SetTexture( "AerialPerspectiveLUT", aerialPerspectiveLuminance );
		drawAerial.SetTexture( "TransmittanceLUT3D", aerialPerspectiveTransmittance );
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

	/// <summary>
	/// Bakes Hillaire's multiple-scattering LUT. Init-time rather than per-frame because Psi_ms
	/// depends only on altitude and sun elevation, which the parameterisation already spans -
	/// nothing changes as the sun moves or the camera flies. Must follow the transmittance LUT,
	/// which it marches against.
	/// </summary>
	void InitAndRenderMultipleScatteringLUT()
	{
		if ( multipleScatteringLUTCompute == null ) { return; }

		// SFloat, unlike the transmittance LUT's UNorm - Psi_ms is unbounded radiance, not a
		// fraction, and a UNorm would clamp the bright end silently.
		ComputeHelper.CreateRenderTexture( ref multipleScatteringLUT, multipleScatteringLUTSize.x, multipleScatteringLUTSize.y,
			FilterMode.Bilinear, GraphicsFormat.R16G16B16A16_SFloat, "Multiple Scattering LUT" );

		multipleScatteringLUTCompute.SetTexture( 0, "MultipleScatteringResult", multipleScatteringLUT );
		multipleScatteringLUTCompute.SetTexture( 0, "TransmittanceLUT", transmittanceLUT );
		ComputeHelper.Dispatch( multipleScatteringLUTCompute, multipleScatteringLUT );
	}

	/// <summary>
	/// Creates the sky view LUT. Filled per frame by RenderSkyViewLUT, not here, since unlike the
	/// other two LUTs it depends on the sun's elevation.
	/// </summary>
	void InitSkyViewLUT()
	{
		if ( skyViewLUTCompute == null ) { return; }

		// SFloat: this is a radiance like the sky texture, not a fraction like the transmittance LUT.
		ComputeHelper.CreateRenderTexture( ref skyViewLUT, skyViewLUTSize.x, skyViewLUTSize.y,
			FilterMode.Bilinear, GraphicsFormat.R16G16B16A16_SFloat, "Sky View LUT" );
	}

	void InitAeiralPerspectiveLUTs()
	{
		GraphicsFormat aerialPerspectiveLUTFormat = GraphicsFormat.R16G16B16A16_SFloat;
		GraphicsFormat transmittance3DFormat = GraphicsFormat.R16G16B16A16_UNorm;
		ComputeHelper.CreateRenderTexture3D( ref aerialPerspectiveLuminance, aerialPerspectiveLUTSize, aerialPerspectiveLUTFormat, TextureWrapMode.Clamp, "Aerial Perspective" );
		ComputeHelper.CreateRenderTexture3D( ref aerialPerspectiveTransmittance, aerialPerspectiveLUTSize, transmittance3DFormat, TextureWrapMode.Clamp, "Transmittance LUT 3D" );
	}

	void RenderAerialPerspectiveLUTs( Camera cam, FrustumCorners corners )
	{
		// Guarded like RenderSkyViewLUT below - all three run from Camera.onPreCull, so an
		// unresolved sun would throw once per camera per frame from inside Unity's render loop.
		if ( aerialPerspectiveLUTCompute == null || light == null ) { return; }

		// Assign dynamic values
		// nearClip/farClip used to be set here; the depth axis is normalised per ray against the
		// atmosphere chord now, not the camera's clip planes.
		SetRaymarchParams( aerialPerspectiveLUTCompute, corners );
		aerialPerspectiveLUTCompute.SetVector( ShaderParamID.dirToSun, -light.transform.forward );

		// 2D over (x, y) only - each thread walks the depth slices itself, so a 3D dispatch would
		// have multiple threads writing the same voxels.
		ComputeHelper.Dispatch( aerialPerspectiveLUTCompute, aerialPerspectiveLUTSize, aerialPerspectiveLUTSize, 1 );
	}


	void InitSkyLUT()
	{
		GraphicsFormat skyFormat = GraphicsFormat.R16G16B16A16_SFloat;
		// No mip chain: ComputeHelper sets autoGenerateMips false and nothing calls GenerateMips,
		// so the levels below 0 are undefined. DrawSky samples with tex2D, which could pick an
		// undefined mip once the sky texture is minified - not an issue at today's sizes, but not
		// guaranteed by the code.
		ComputeHelper.CreateRenderTexture( ref sky, skyRenderSize.x, skyRenderSize.y, FilterMode.Bilinear, skyFormat, "Sky" );
	}

	/// <summary>
	/// Re-points the compute shaders at their textures and constants. Bindings don't survive a
	/// domain reload or the editor losing focus, and nothing else restores them - EditorShaderHelper
	/// only covers focus changes. Refreshed on every SetProperties; only the expensive dispatch
	/// stays behind the dirty flag.
	/// </summary>
	void BindComputeResources()
	{
		// Every raymarch() consumer needs the multiple-scattering LUT bound, including via
		// ApplyAtmosphereValuesTo - an unbound compute texture fails silently rather than loudly.
		if ( multipleScatteringLUT != null )
		{
			if ( material != null ) { material.SetTexture( "MultipleScatteringLUT", multipleScatteringLUT ); }
			if ( skyRenderCompute != null ) { skyRenderCompute.SetTexture( 0, "MultipleScatteringLUT", multipleScatteringLUT ); }
			if ( aerialPerspectiveLUTCompute != null ) { aerialPerspectiveLUTCompute.SetTexture( 0, "MultipleScatteringLUT", multipleScatteringLUT ); }
		}

		// The cloud deck, for light shafts in raymarchSegment. Bound explicitly since computes
		// don't receive Shader.SetGlobalTexture - every kernel referencing CloudShadowMap needs
		// an explicit bind or the dispatch fails outright. Rebound every frame since the owning
		// renderer can change between profiles.
		CloudShadowGlobals.BindTo( skyViewLUTCompute, 0 );
		CloudShadowGlobals.BindTo( skyRenderCompute, 0 );
		CloudShadowGlobals.BindTo( aerialPerspectiveLUTCompute, 0 );

		if ( skyViewLUTCompute != null && skyViewLUT != null )
		{
			skyViewLUTCompute.SetTexture( 0, "SkyViewResult", skyViewLUT );
			skyViewLUTCompute.SetTexture( 0, "TransmittanceLUT", transmittanceLUT );
			if ( multipleScatteringLUT != null ) { skyViewLUTCompute.SetTexture( 0, "MultipleScatteringLUT", multipleScatteringLUT ); }
			skyViewLUTCompute.SetInt( "numSkyViewSteps", numSkyViewScatteringSteps );
		}

		if ( skyRenderCompute != null && sky != null )
		{
			skyRenderCompute.SetTexture( 0, "TransmittanceLUT", transmittanceLUT );
			skyRenderCompute.SetTexture( 0, "Sky", sky );
			skyRenderCompute.SetInt( "numScatteringSteps", numSkyScatteringSteps );
			skyRenderCompute.SetInts( "size", skyRenderSize.x, skyRenderSize.y );
		}

		if ( aerialPerspectiveLUTCompute != null && aerialPerspectiveLuminance != null )
		{
			aerialPerspectiveLUTCompute.SetTexture( 0, "AerialPerspectiveLuminance", aerialPerspectiveLuminance );
			aerialPerspectiveLUTCompute.SetTexture( 0, "AerialPerspectiveTransmittance", aerialPerspectiveTransmittance );
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

		SetRaymarchParams( skyRenderCompute, corners );
		skyRenderCompute.SetVector( ShaderParamID.dirToSun, -light.transform.forward );
		ComputeHelper.Dispatch( skyRenderCompute, sky );
	}

	/// <summary>
	/// Fills the sky view LUT for this frame's sun elevation, in a canonical frame (up=+Y, sun in
	/// the +X/+Y half-plane) fabricated here since HLSL globals are read-only in-shader and
	/// raymarch reads dirToSun directly (MultipleScattering.compute does the same). Exact by
	/// spherical symmetry, but the LUT holds only the reference point's sun elevation while the
	/// visible ocean spans up to 20 degrees of arc - a third LUT axis is the clean fix, noted in
	/// NOTES.
	/// </summary>
	void RenderSkyViewLUT( Camera cam )
	{
		if ( skyViewLUTCompute == null || skyViewLUT == null || light == null ) { return; }

		Vector3 up = cam.transform.position.normalized;
		float cosSunZenith = Mathf.Clamp( Vector3.Dot( up, -light.transform.forward ), -1f, 1f );
		float sinSunZenith = Mathf.Sqrt( Mathf.Max( 0f, 1f - cosSunZenith * cosSunZenith ) );

		skyViewLUTCompute.SetVector( ShaderParamID.dirToSun, new Vector3( sinSunZenith, cosSunZenith, 0f ) );
		ComputeHelper.Dispatch( skyViewLUTCompute, skyViewLUT );
	}

	/// <summary>
	/// The frustum's four corner directions and the camera position, computed once per frame and
	/// shared by both consumers rather than each inverting the projection matrix per corner.
	/// </summary>
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

	static void SetRaymarchParams( ComputeShader raymarchCompute, FrustumCorners corners )
	{
		raymarchCompute.SetVector( ShaderParamID.topLeftDir, corners.topLeft );
		raymarchCompute.SetVector( ShaderParamID.topRightDir, corners.topRight );
		raymarchCompute.SetVector( ShaderParamID.bottomLeftDir, corners.bottomLeft );
		raymarchCompute.SetVector( ShaderParamID.bottomRightDir, corners.bottomRight );
		raymarchCompute.SetVector( ShaderParamID.camPos, corners.camPos );
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
		// multipleScatteringLUT and skyViewLUT were both missing from this list.
		ComputeHelper.Release( aerialPerspectiveLuminance, sky, transmittanceLUT, aerialPerspectiveTransmittance,
			multipleScatteringLUT, skyViewLUT );
	}



	/// <summary>
	/// Forces the LUTs to rebuild next frame. OnValidate covers inspector edits, but runtime
	/// writes to these fields (the world-scale presets) have no such hook.
	/// </summary>
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

		/// <summary>
		/// Pushes the same values as globals, for shaders this effect doesn't own (the ocean's
		/// sun-glint tint). Globals win over a material reference because several of these values -
		/// planetRadius, atmosphereRadius, intensity, contrast, whitePoint - change with the active
		/// world-scale preset. Per-material properties still shadow globals, so nothing that sets
		/// these on itself is affected; a future shader declaring one of these generic names would
		/// inherit it, though.
		/// </summary>
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
