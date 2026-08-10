using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

[CreateAssetMenu(menuName = "PostProcessing/Atmosphere")]
public class AtmosphereEffect : PostProcessingEffect
{

	public Shader drawSkyShader;

	public Color sunColTest;
	public Light light;

	public float bodyRadius;
	public float atmosphereThickness;

	public Shader atmosphereShader;
	public Vector4 testParams = new Vector4(7, 1.26f, 0.1f, 3);

	/// <summary>
	/// Raymarch steps per depth slice of the aerial perspective LUT.
	///
	/// Was `numAerialScatteringSteps`, the steps for a whole march from the atmosphere boundary
	/// to each slice - which meant the far slices covered the entire clip distance in 20 steps.
	/// The march is now incremental, so this is per slice and the total is size x this.
	/// </summary>
	public int aerialStepsPerSlice = 6;


	[Header("Rayleigh Scattering")]
	// Wavelengths of red, green, and blue light (nanometres)
	public Vector3 wavelengthsRGB = new Vector3(700, 530, 460);
	// Scale value to adjust all wavelengths at once
	public float wavelengthScale = 300;
	// Altitude [0, 1] at which the average density of particles causing rayleigh scattering is found
	[Range(0, 1)] public float rayleighDensityAvg = 0.1f;

	[Header("Mie Scattering")]
	// Altitude [0, 1] at which the average density of particles causing mie scattering is found
	[Range(0, 1)] public float mieDensityAvg = 0.1f;
	// Strength of mie scattering
	public float mieCoefficient;
	// Strength of mie absorption
	public float mieAbsorption;
	/// <summary>
	/// Asymmetry of the Cornette-Shanks phase function. 0 is isotropic (and reduces exactly to
	/// the Rayleigh phase); higher values scatter more sharply forward. Was hardcoded at 0.8.
	/// </summary>
	[Range(0, 0.99f)] public float mieAsymmetry = 0.8f;

	[Header("Density")]
	/// <summary>
	/// Multiplies every scattering and absorption coefficient at once - i.e. how much denser
	/// this atmosphere is than Earth's, at the same composition and the same vertical structure.
	///
	/// This exists because the planet is too small for Earth-calibrated coefficients to produce
	/// a sunset. Reddening needs blue extinguished along the slant path to the horizon, and the
	/// amplification of that path over the vertical one is sqrt(pi*R/2H) - about 12 here against
	/// Earth's 35, because the planet is 750 km rather than 6371. The shortfall has to be made up
	/// somewhere, and density is the honest place: one named number with a stated reason, rather
	/// than six constants quietly bent away from their published values.
	///
	/// The value is not free either. It is set so the horizon optical depth in blue matches
	/// Earth's, which is the quantity a sunset is actually made of - the validation harness
	/// reports both and the ratio between them.
	/// </summary>
	public float densityMultiplier = 1;

	[Header("Illumination")]
	/// <summary>
	/// Illuminance arriving at the top of the atmosphere. 4*PI is the value at which the
	/// normalised Rayleigh phase reproduces the average in-scatter the old hardcoded
	/// `rayleighPhaseValue = 1` produced, so it is the natural starting point rather than a
	/// physical measurement - the units here are not yet tied to SI radiometry.
	/// </summary>
	public float sunIlluminance = 4 * Mathf.PI;

	[Header("Ozone")]
	//Altitude [0, 1] at which ozone density is at the greatest
	[Range(0, 1)] public float ozonePeakDensityAltitude = 0.25f;
	[Range(0, 10)] public float ozoneDensityFalloff = 4;
	[Range(0, 5)] public float ozoneStrength = 1;
	public Vector3 ozoneAbsorption;

	[Header("Sun Disc")]
	public float sunDiscSize;
	public float sunDiscBlurA;
	public float sunDiscBlurB;

	[Header("Transmittance LUT (2D)")]
	public ComputeShader transmittanceLUTCompute;
	public Vector2Int transmittanceLUTSize;

	[Header("Multiple Scattering LUT (2D)")]
	public ComputeShader multipleScatteringLUTCompute;
	/// <summary>
	/// 32x32 is Hillaire's size and is ample: Psi_ms is smooth in both altitude and sun
	/// elevation, having had every angular feature averaged out of it by construction.
	/// </summary>
	public Vector2Int multipleScatteringLUTSize = new Vector2Int(32, 32);
	/// <summary>
	/// 0 turns multiple scattering off, leaving single scattering alone. This is the control
	/// condition for the comparison, so it is a runtime value rather than a shader variant.
	/// </summary>
	[Range(0, 2)] public float multipleScatteringStrength = 1;
	/// <summary>
	/// Ground reflectance used by the Lambertian bounce inside the LUT. 0.1 is Hillaire's
	/// default and is about right for ocean-and-land average.
	/// </summary>
	[Range(0, 1)] public float groundAlbedo = 0.1f;

	[Header("Aerial Perpspective LUT")]
	public ComputeShader aerialPerspectiveLUTCompute;
	public int aerialPerspectiveLUTSize;
	// Allow control over how strongly atmosphere affects appearance of terrain
	[Range(0, 1)] public float aerialPerspectiveStrength = 1;


	[Header("Sky Texture")]
	// Num raymarch steps when drawing the sky (this is drawn small and upscaled, so can afford to be fairly high)
	public int numSkyScatteringSteps = 100;
	public ComputeShader skyRenderCompute;
	// Note: since sky colours change quite smoothly this can be very small (e.g. 128x64)
	// However, the vertical resolution should be increased (~128x256) so that earth shadow isn't too jaggedy
	public Vector2Int skyRenderSize;
	// Allow control over how strongly atmosphere affects appearance of objects in the sky (moon, stars)
	[Range(0, 1)] public float skyTransmittanceWeight = 1;

	[Header("Tone mapping")]
	public float intensity = 1;
	public float contrast = 1.45f;
	public float whitePoint = 1.1f;


	[Header("Later")]
	public float ditherStrength = 0.8f;
	public Texture2D blueNoise;
	public FilterMode filterMode;

	[Header("Debug")]
	/// <summary>
	/// Derived from wavelengthsRGB and wavelengthScale, shown for inspection only - editing it
	/// does nothing, it is overwritten on the next settings update.
	///
	/// It is serialized, so writing it from GetShaderValues dirtied the asset on every call.
	/// It is now computed into a local and mirrored here once, which keeps the inspector
	/// readout without the write-back.
	/// </summary>
	public Vector3 rayleighCoefficients;

	[Header(("Debug"))]
	public RenderTexture transmittanceLUT;
	public RenderTexture multipleScatteringLUT;
	public RenderTexture aerialPerspectiveLuminance;
	public RenderTexture aerialPerspectiveTransmittance;//
	public RenderTexture sky;
	bool settingsUpToDate;

	ShaderValues sharedAtmosphereValues;
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

	public void SetupSkyRenderingCommand(CommandBuffer skyRenderCommand)
	{
		lutUpdateRequired = true;

		// HideAndDontSave: this is recreated on every domain reload under [ExecuteInEditMode],
		// and without it each reload leaked a material.
		if (drawSkyMaterial == null || drawSkyMaterial.shader != drawSkyShader)
		{
			drawSkyMaterial = new Material(drawSkyShader) { hideFlags = HideFlags.HideAndDontSave };
		}

		// Shared with the baseline sky, so the two passes cannot drift apart.
		SkyPass.Record(skyRenderCommand, drawSkyMaterial);

		SetDrawSkyShaderParameters(drawSkyMaterial);
	}


	// Called on camera pre-cull
	void RenderLUTs(Camera activeCamera)
	{

		if (lutUpdateRequired)
		{
			if (activeCamera == cam)
			{
				lutUpdateRequired = false;
				RenderSky(activeCamera);
				RenderAerialPerspectiveLUTs(activeCamera);
			}
		}
	}

	void OnDisable()
	{
		// This effect owns no command buffer. RenderingManager creates the sky buffer, passes
		// it to SetupSkyRenderingCommand to be filled, and owns its lifetime - so there is
		// nothing here to release, and the RemoveAllCommandBuffers() that used to be here was
		// pure collateral damage: as a ScriptableObject this fires on domain reload, and it
		// detached the stars and the moon along with the sky while RenderingManager still
		// believed all three were attached. Its Update only reacts to `enabled` *changing*,
		// so it never repaired them.
		Camera.onPreCull -= RenderLUTs;
	}

	protected override void RenderEffectToTarget(RenderTexture source, RenderTexture target)
	{
		SetProperties();
		lutUpdateRequired = true;
		Graphics.Blit(source, target, material);
	}

	public void SetProperties()
	{

		if (light == null)
		{
			GameObject sunObject = GameObject.FindGameObjectWithTag("Sun");
			light = sunObject?.GetComponent<Light>();
		}

		if (material != null && light != null)
		{
			material.SetVector(ShaderParamID.dirToSun, -light.transform.forward);
		}
		// Was `!settingsUpToDate || !Application.isPlaying`, so in edit mode the whole init
		// branch - including the transmittance LUT dispatch - ran on EVERY frame. That is a
		// measurement confound and it grows once there is a multiple-scattering LUT to rebuild
		// too. The dirty flag alone is sufficient: OnValidate sets it on any inspector edit,
		// OnEnable clears it on load and domain reload, and EditorShaderHelper sets it when the
		// editor regains focus and compute bindings are lost.
		//
		// The null check is the belt: a lost render texture is the one case that would not
		// otherwise re-trigger, and it costs one reference comparison per frame.
		// Bindings are refreshed unconditionally: they are cheap, and they do not survive a
		// domain reload. The expensive part - creating render textures and dispatching the
		// transmittance LUT - stays behind the dirty flag.
		BindComputeResources();

		if (!settingsUpToDate || transmittanceLUT == null || multipleScatteringLUT == null)
		{
			sharedAtmosphereValues = GetShaderValues();
			sharedAtmosphereValues.Apply(material);
			if (multipleScatteringLUTCompute != null) { sharedAtmosphereValues.Apply(multipleScatteringLUTCompute); }
			sharedAtmosphereValues.Apply(transmittanceLUTCompute);
			sharedAtmosphereValues.Apply(aerialPerspectiveLUTCompute);
			sharedAtmosphereValues.Apply(skyRenderCompute);


			InitAndRenderTransmittanceLUT();
			InitAndRenderMultipleScatteringLUT();
			InitAeiralPerspectiveLUTs();
			InitSkyLUT();

			// Again, after the multiple-scattering LUT exists: BindComputeResources ran before
			// this branch, when the texture was still null.
			BindComputeResources();

			// Set shader params after all LUTs have been initialized
			SetDrawAerialPerspectiveShaderParams(material);

			// Draw sky settings
			if (drawSkyMaterial != null)
			{
				SetDrawSkyShaderParameters(drawSkyMaterial);
			}

			// Done
			settingsUpToDate = true;
			onSettingsUpdated?.Invoke();
		}
	}

	/// <summary>
	/// Applies the current atmosphere parameters to an arbitrary compute shader, after making
	/// sure the LUTs they depend on are up to date.
	///
	/// Exists for the baseline renderer's offline sky bake, which evaluates the *same*
	/// scattering code as the runtime renderer rather than a reimplementation of it. If the
	/// bake carried its own copy of the physics, a difference between the baked baseline and
	/// the physically based sky could be a difference in the bake rather than in the
	/// technique - which is exactly the confound the comparison cannot afford.
	/// </summary>
	public void ApplyAtmosphereValuesTo(ComputeShader compute)
	{
		// In edit mode SetProperties always takes its init branch, so this also guarantees
		// transmittanceLUT has been rendered.
		SetProperties();
		GetShaderValues().Apply(compute);

		// The bakers call raymarch(), so they need the multiple-scattering LUT bound like every
		// other consumer. This matters more than it looks: `baseline-baked` exists to be the
		// physically based sky flattened into a texture, so if the bake omitted a term the
		// runtime has, the comparison would be measuring the bake rather than the technique.
		if (multipleScatteringLUT != null) { compute.SetTexture(0, "MultipleScatteringLUT", multipleScatteringLUT); }
	}

	ShaderValues GetShaderValues()
	{
		ShaderValues values = new ShaderValues();
		// Size values
		values.floats.Add(("atmosphereThickness", atmosphereThickness));
		values.floats.Add(("atmosphereRadius", bodyRadius + atmosphereThickness));
		values.floats.Add(("planetRadius", bodyRadius));

		// The transmittance mapping divides by this on both the write and the read, so it has to
		// be one value rather than two int uniforms that could drift apart.
		values.vectors.Add(("transmittanceLutSize", new Vector2(transmittanceLUTSize.x, transmittanceLUTSize.y)));

		// The shader now works in absolute altitude and per-world-unit coefficients, so the
		// inspector's normalised fields are converted here rather than inside the march.
		//
		// The inspector fields are still fractions of the atmosphere thickness. Expressing
		// them in kilometres is part of adopting physical constants; converting here first
		// keeps this step numerically identical, which is what makes it checkable.
		float thickness = atmosphereThickness;

		// Rayleigh values
		values.floats.Add(("rayleighScaleHeight", rayleighDensityAvg * thickness));
		// Arbitrary scale to give nicer range of reasonable values for the scattering constant
		// Strength of (rayleigh) scattering is dependent on wavelength (~ 1/wavelength^4)
		Vector3 inverseWavelengths = new Vector3(1 / wavelengthsRGB.x, 1 / wavelengthsRGB.y, 1 / wavelengthsRGB.z);
		Vector3 rayleigh = Pow(inverseWavelengths * wavelengthScale, 4);
		values.vectors.Add(("rayleighCoefficients", rayleigh * densityMultiplier / thickness));

		// Mirror into the serialized debug field only when it has actually changed. Writing it
		// unconditionally from here marked the asset dirty on every settings update.
		if (rayleighCoefficients != rayleigh) { rayleighCoefficients = rayleigh; }

		// Mie values
		values.floats.Add(("mieScaleHeight", mieDensityAvg * thickness));
		values.floats.Add(("mieCoefficient", mieCoefficient * densityMultiplier / thickness));
		values.floats.Add(("mieAbsorption", mieAbsorption * densityMultiplier / thickness));
		values.floats.Add(("mieAsymmetry", mieAsymmetry));

		// Dimensionless, so no thickness conversion - it multiplies in-scatter, never optical
		// depth. That asymmetry is the whole reason it can fix the phase without disturbing
		// transmittance, which no change to a scattering coefficient could have done.
		values.floats.Add(("sunIlluminance", sunIlluminance));

		// Multiple scattering. The LUT size goes to the shader as a float2 because both the
		// write and the read divide by it; sending it as an int pair would mean two conversions
		// that have to agree.
		values.vectors.Add(("multipleScatteringLutSize", new Vector2(multipleScatteringLUTSize.x, multipleScatteringLUTSize.y)));
		values.floats.Add(("multipleScatteringStrength", multipleScatteringStrength));
		values.floats.Add(("groundAlbedo", groundAlbedo));

		// Ozone values. The tent was `1 - |peak01 - h01| * falloff` in normalised altitude, so
		// its half-width in world units is thickness / falloff.
		values.floats.Add(("ozonePeakAltitude", ozonePeakDensityAltitude * thickness));
		values.floats.Add(("ozoneHalfWidth", thickness / Mathf.Max(1e-4f, ozoneDensityFalloff)));
		values.vectors.Add(("ozoneAbsorption", ozoneAbsorption * ozoneStrength * densityMultiplier * 0.1f / thickness));

		return values;
	}

	void SetDrawSkyShaderParameters(Material drawSky)
	{
		// Textures
		drawSky.SetTexture("Sky", sky);
		drawSky.SetTexture("TransmittanceLUT", transmittanceLUT);
		drawSky.SetTexture("BlueNoise", blueNoise);

		// Values
		drawSky.SetFloat("atmosphereThickness", atmosphereThickness);
		drawSky.SetFloat("planetRadius", bodyRadius);
		// Bruneton's parameterisation needs both of these. The radius was already being set in
		// anticipation; the size is new, and a silent zero here would divide the whole mapping
		// by nothing and put every sample in one corner of the LUT.
		drawSky.SetFloat("atmosphereRadius", bodyRadius + atmosphereThickness);
		drawSky.SetVector("transmittanceLutSize", new Vector2(transmittanceLUTSize.x, transmittanceLUTSize.y));
		drawSky.SetFloat("sunDiscSize", sunDiscSize);
		drawSky.SetFloat("sunDiscBlurA", sunDiscBlurA);
		drawSky.SetFloat("sunDiscBlurB", sunDiscBlurB);
		drawSky.SetFloat("ditherStrength", ditherStrength);
		drawSky.SetFloat("intensity", intensity);
		drawSky.SetFloat("contrast", contrast);
		drawSky.SetFloat("whitePoint", whitePoint);
		drawSky.SetFloat("skyTransmittanceWeight", skyTransmittanceWeight);
	}


	void SetDrawAerialPerspectiveShaderParams(Material drawAerial)
	{
		// Textures
		drawAerial.SetTexture("AerialPerspectiveLUT", aerialPerspectiveLuminance);
		drawAerial.SetTexture("TransmittanceLUT3D", aerialPerspectiveTransmittance);
		drawAerial.SetTexture("BlueNoise", blueNoise);

		// Values
		drawAerial.SetVector("params", testParams);
		drawAerial.SetFloat("ditherStrength", ditherStrength);
		drawAerial.SetFloat("aerialPerspectiveStrength", aerialPerspectiveStrength);

		drawAerial.SetFloat("intensity", intensity);
		drawAerial.SetFloat("contrast", contrast);
		drawAerial.SetFloat("whitePoint", whitePoint);
	}

	// Create lookup texture for the transmittance (proportion of light reaching given point through the atmosphere)
	// This only needs to be created once at the start (or whenever atmosphere parameters are changed)
	void InitAndRenderTransmittanceLUT()
	{
		GraphicsFormat transmittanceLUTFormat = GraphicsFormat.R16G16B16A16_UNorm;//
		ComputeHelper.CreateRenderTexture(ref transmittanceLUT, transmittanceLUTSize.x, transmittanceLUTSize.y, FilterMode.Bilinear, transmittanceLUTFormat, "Transmittance LUT");
		transmittanceLUTCompute.SetTexture(0, "TransmittanceLUT", transmittanceLUT);
		transmittanceLUTCompute.SetInt("width", transmittanceLUTSize.x);
		transmittanceLUTCompute.SetInt("height", transmittanceLUTSize.y);
		ComputeHelper.Dispatch(transmittanceLUTCompute, transmittanceLUT);
	}

	/// <summary>
	/// Bakes Hillaire's multiple-scattering LUT.
	///
	/// Init-time, not per-frame, and that is a property of the quantity rather than an
	/// optimisation: Psi_ms depends only on altitude and sun elevation, both of which the
	/// parameterisation already spans, so nothing about it changes as the sun moves or the
	/// camera flies. It has to be rebuilt when the atmosphere parameters change, which is
	/// exactly when this branch runs.
	///
	/// Must follow the transmittance LUT, which it marches against.
	/// </summary>
	void InitAndRenderMultipleScatteringLUT()
	{
		if (multipleScatteringLUTCompute == null) { return; }

		// SFloat, unlike the transmittance LUT's UNorm. Psi_ms is a radiance, not a fraction,
		// and is not bounded to [0, 1] - a UNorm would clamp the bright end silently.
		ComputeHelper.CreateRenderTexture(ref multipleScatteringLUT, multipleScatteringLUTSize.x, multipleScatteringLUTSize.y,
			FilterMode.Bilinear, GraphicsFormat.R16G16B16A16_SFloat, "Multiple Scattering LUT");

		multipleScatteringLUTCompute.SetTexture(0, "MultipleScatteringResult", multipleScatteringLUT);
		multipleScatteringLUTCompute.SetTexture(0, "TransmittanceLUT", transmittanceLUT);
		ComputeHelper.Dispatch(multipleScatteringLUTCompute, multipleScatteringLUT);
	}

	void InitAeiralPerspectiveLUTs()
	{
		GraphicsFormat aerialPerspectiveLUTFormat = GraphicsFormat.R16G16B16A16_SFloat;
		GraphicsFormat transmittance3DFormat = GraphicsFormat.R16G16B16A16_UNorm;
		ComputeHelper.CreateRenderTexture3D(ref aerialPerspectiveLuminance, aerialPerspectiveLUTSize, aerialPerspectiveLUTFormat, TextureWrapMode.Clamp, "Aerial Perspective");
		ComputeHelper.CreateRenderTexture3D(ref aerialPerspectiveTransmittance, aerialPerspectiveLUTSize, transmittance3DFormat, TextureWrapMode.Clamp, "Transmittance LUT 3D");

		BindComputeResources();
	}

	void RenderAerialPerspectiveLUTs(Camera cam)
	{
		// Assign dynamic values
		SetRaymarchParams(cam, aerialPerspectiveLUTCompute);
		aerialPerspectiveLUTCompute.SetFloat(ShaderParamID.nearClip, cam.nearClipPlane);
		aerialPerspectiveLUTCompute.SetFloat(ShaderParamID.farClip, cam.farClipPlane);
		aerialPerspectiveLUTCompute.SetVector(ShaderParamID.dirToSun, -light.transform.forward);

		// 2D, over (x, y) only: each thread now walks the depth slices itself, so dispatching
		// over the volume would launch `size` threads per column all writing the same voxels.
		ComputeHelper.Dispatch(aerialPerspectiveLUTCompute, aerialPerspectiveLUTSize, aerialPerspectiveLUTSize, 1);
	}


	void InitSkyLUT()
	{
		GraphicsFormat skyFormat = GraphicsFormat.R16G16B16A16_SFloat;
		ComputeHelper.CreateRenderTexture(ref sky, skyRenderSize.x, skyRenderSize.y, FilterMode.Bilinear, skyFormat, "Sky", useMipMaps: true);

		BindComputeResources();
	}

	/// <summary>
	/// Re-points the compute shaders at their textures and constants. Cheap, and separate from
	/// the render textures' creation on purpose.
	///
	/// Compute shader bindings do not survive a domain reload or the editor losing focus, and
	/// nothing else restores them: `EditorShaderHelper` covers focus changes but not every path
	/// that drops them. Until the per-frame re-init was removed this was masked, because that
	/// re-ran the whole init - including these binds - on every editor frame. Removing the
	/// per-frame *dispatch* was worth doing; removing the per-frame *rebind* was not, and it
	/// showed up immediately as "Property (TransmittanceLUT) at kernel index (0) is not set".
	///
	/// So bindings are refreshed on every SetProperties and only the dispatch stays behind the
	/// dirty flag.
	/// </summary>
	void BindComputeResources()
	{
		// Every consumer of raymarch() now reads the multiple-scattering LUT, so every one of
		// them has to have it bound - including the material, and including the offline bakers
		// via ApplyAtmosphereValuesTo. A compute with an unbound texture does not fail loudly.
		if (multipleScatteringLUT != null)
		{
			if (material != null) { material.SetTexture("MultipleScatteringLUT", multipleScatteringLUT); }
			if (skyRenderCompute != null) { skyRenderCompute.SetTexture(0, "MultipleScatteringLUT", multipleScatteringLUT); }
			if (aerialPerspectiveLUTCompute != null) { aerialPerspectiveLUTCompute.SetTexture(0, "MultipleScatteringLUT", multipleScatteringLUT); }
		}

		if (skyRenderCompute != null && sky != null)
		{
			skyRenderCompute.SetTexture(0, "TransmittanceLUT", transmittanceLUT);
			skyRenderCompute.SetTexture(0, "Sky", sky);
			skyRenderCompute.SetInt("numScatteringSteps", numSkyScatteringSteps);
			skyRenderCompute.SetInts("size", skyRenderSize.x, skyRenderSize.y);
		}

		if (aerialPerspectiveLUTCompute != null && aerialPerspectiveLuminance != null)
		{
			aerialPerspectiveLUTCompute.SetTexture(0, "AerialPerspectiveLuminance", aerialPerspectiveLuminance);
			aerialPerspectiveLUTCompute.SetTexture(0, "AerialPerspectiveTransmittance", aerialPerspectiveTransmittance);
			aerialPerspectiveLUTCompute.SetTexture(0, "TransmittanceLUT", transmittanceLUT);
			aerialPerspectiveLUTCompute.SetInt("size", aerialPerspectiveLUTSize);
			aerialPerspectiveLUTCompute.SetInt("stepsPerSlice", aerialStepsPerSlice);
		}
	}

	// Render the sky to a small texture, which then will be upscaled to reduce expensive raymarching
	// This is rendered every frame
	void RenderSky(Camera cam)
	{
		SetRaymarchParams(cam, skyRenderCompute);
		skyRenderCompute.SetVector(ShaderParamID.dirToSun, -light.transform.forward);
		ComputeHelper.Dispatch(skyRenderCompute, sky);
	}

	void SetRaymarchParams(Camera cam, ComputeShader raymarchCompute)
	{
		Vector3 topLeftDir = CalculateViewDirection(cam, new Vector2(0, 1));
		Vector3 topRightDir = CalculateViewDirection(cam, new Vector2(1, 1));
		Vector3 bottomLeftDir = CalculateViewDirection(cam, new Vector2(0, 0));
		Vector3 bottomRightDir = CalculateViewDirection(cam, new Vector2(1, 0));

		raymarchCompute.SetVector(ShaderParamID.topLeftDir, topLeftDir);
		raymarchCompute.SetVector(ShaderParamID.topRightDir, topRightDir);
		raymarchCompute.SetVector(ShaderParamID.bottomLeftDir, bottomLeftDir);
		raymarchCompute.SetVector(ShaderParamID.bottomRightDir, bottomRightDir);
		raymarchCompute.SetVector(ShaderParamID.camPos, cam.transform.position);
	}

	public static class ShaderParamID
	{
		public static int topLeftDir = Shader.PropertyToID("topLeftDir");
		public static int topRightDir = Shader.PropertyToID("topRightDir");
		public static int bottomLeftDir = Shader.PropertyToID("bottomLeftDir");
		public static int bottomRightDir = Shader.PropertyToID("bottomRightDir");
		public static int camPos = Shader.PropertyToID("camPos");
		public static int nearClip = Shader.PropertyToID("nearClip");
		public static int farClip = Shader.PropertyToID("farClip");

		public static int dirToSun = Shader.PropertyToID("dirToSun");

	}

	Vector3 CalculateViewDirection(Camera camera, Vector2 texCoord)
	{
		Matrix4x4 camInverseMatrix = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true).inverse;
		Matrix4x4 localToWorldMatrix = camera.transform.localToWorldMatrix;

		Vector3 viewVector = camInverseMatrix * new Vector4(texCoord.x * 2 - 1, texCoord.y * 2 - 1, 0, -1);
		viewVector = localToWorldMatrix * new Vector4(viewVector.x, viewVector.y, viewVector.z, 0);
		return viewVector.normalized;
	}

	public override void OnDestroy()
	{
		ComputeHelper.Release(aerialPerspectiveLuminance, sky, transmittanceLUT, aerialPerspectiveTransmittance);
	}



	/// <summary>
	/// Forces the LUTs to be rebuilt on the next frame.
	///
	/// OnValidate covers inspector edits, which is every edit-mode change. Anything that writes
	/// these fields from script at runtime - the world-scale presets - has no such hook, and
	/// without this the transmittance and multiple-scattering LUTs would keep describing the
	/// previous atmosphere while every other uniform described the new one.
	/// </summary>
	public void MarkSettingsDirty() => settingsUpToDate = false;

	void OnValidate()
	{
		if (Application.isEditor)
		{
			settingsUpToDate = false;
		}
	}

	Vector3 Pow(Vector3 vector, float power)
	{
		return new Vector3(Mathf.Pow(vector.x, power), Mathf.Pow(vector.y, power), Mathf.Pow(vector.z, power));
	}

	void EditorOnlyInit()
	{
#if UNITY_EDITOR
		EditorShaderHelper.onRebindRequired += () => settingsUpToDate = false;
#endif
	}//



	public class ShaderValues
	{
		public List<(string name, float value)> floats;
		public List<(string name, int value)> ints;
		public List<(string name, Vector4 value)> vectors;

		public ShaderValues()
		{
			floats = new List<(string name, float value)>();
			ints = new List<(string name, int value)>();
			vectors = new List<(string name, Vector4 value)>();
		}

		public void Apply(Material material)
		{
			foreach (var data in floats)
			{
				material.SetFloat(data.name, data.value);
			}

			foreach (var data in ints)
			{
				material.SetInt(data.name, data.value);
			}

			foreach (var data in vectors)
			{
				material.SetVector(data.name, data.value);
			}
		}

		public void Apply(ComputeShader compute)
		{
			foreach (var data in floats)
			{
				compute.SetFloat(data.name, data.value);
			}

			foreach (var data in ints)
			{
				compute.SetInt(data.name, data.value);
			}

			foreach (var data in vectors)
			{
				compute.SetVector(data.name, data.value);
			}
		}


	}
}