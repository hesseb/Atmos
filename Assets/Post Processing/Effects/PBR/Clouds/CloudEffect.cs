using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Serialization;

namespace Clouds
{
	// Volumetric clouds on a spherical shell, following Schneider (Guerrilla).
	[CreateAssetMenu( menuName = "PostProcessing/Clouds" )]
	public class CloudEffect : PostProcessingEffect
	{
		public enum CostMode
		{
			Full,
			Half,
			Temporal,
		}

		public enum Genus
		{
			Off = 0,

			Stratus,
			Stratocumulus,
			Cumulus,
			Cumulonimbus,

			Altostratus,
			Altocumulus,
			Nimbostratus,

			Cirrus,
			Cirrocumulus,
			Cirrostratus,
		}

		public enum DebugMode
		{
			Off = 0,
			Segment = 3,
			Density = 4,
			StartDistance = 5,
			CameraRegion = 6,
			Coverage = 7,
			CloudType = 8,
			Precipitation = 9,
			BaseHeight = 10,
		}

		[Header( "Baked volumes" )]
		public Texture3D shapeNoise;
		public Texture3D detailNoise;

		[Header( "Weather map" )]
		public ComputeShader weatherCompute;
		public Vector2Int weatherMapSize = new Vector2Int( 512, 256 );
		[Range( 1, 8 )] public int weatherOctaves = 5;
		[Range( 1.2f, 4f )] public float weatherLacunarity = 2f;
		[Range( 0.1f, 0.9f )] public float weatherGain = 0.5f;
		[Tooltip( "Shifts all weather fields. A different seed gives different weather." )]
		[Range( 0, 100 )] public int weatherSeed = 0;

		[Tooltip( "Feature size of the coverage field. Lower values give larger weather systems." )]
		[Range( 0.2f, 12f )] public float coverageScale = 2.2f;
		[Tooltip( "The main control for how cloudy the sky is." )]
		[Range( -0.5f, 0.5f )] public float coverageBias = 0f;
		[Tooltip( "Higher values push coverage toward clear sky or overcast." )]
		[Range( 0.2f, 4f )] public float coverageContrast = 1.3f;

		[Tooltip( "Stretches each weather field to fill the 0-1 range, which an FBM does not reach on " +
			"its own. Check the value again if the octave count changes." )]
		[Range( 1f, 4f )] public float weatherRangeGain = 2.3f;

		[Tooltip( "Feature size of the cloud-type field. Type changes on its own scale, larger than " +
			"the coverage scale." )]
		[Range( 0.1f, 6f )] public float typeScale = 1.1f;
		[Range( -0.5f, 0.5f )] public float typeBias = 0f;
		[Tooltip( "Pushes cloud type toward stratus or cumulonimbus. Without it, most regions show cumulus." )]
		[Range( 0.2f, 4f )] public float typeContrast = 1.6f;
		[Tooltip( "Drift speed relative to the coverage field." )]
		[Range( 0f, 3f )] public float typeWindFactor = 0.35f;

		[Range( 0.2f, 12f )] public float precipitationScale = 3.5f;
		[Range( -0.5f, 0.5f )] public float precipitationBias = -0.1f;
		[Range( 0f, 3f )] public float precipitationWindFactor = 0.6f;

		[Tooltip( "Feature size of the field that lifts and lowers the cloud band. At 12-20, some " +
			"clouds are taller than their neighbours. Above about 30, a 512-wide map cannot resolve it." )]
		[Range( 0.2f, 30f )] public float baseHeightScale = 16f;
		[Range( -0.5f, 0.5f )] public float baseHeightBias = 0f;
		[Range( 0f, 3f )] public float baseHeightWindFactor = 0.5f;

		[Tooltip( "Drift speed of the weather fields. They drift through the sphere, not the UV, so " +
			"features do not tear at the poles." )]
		public float weatherWindSpeed = 0.004f;

		[Header( "High cloud weather" )]
		[Tooltip( "Feature size of the ice layer's coverage. High cloud has its own weather, separate " +
			"from the cloud below it." )]
		[Range( 0.2f, 12f )] public float highCoverageScale = 1.6f;

		[Tooltip( "Cirrus is usually sparse, so the default is negative." )]
		[Range( -0.5f, 0.5f )] public float highCoverageBias = -0.15f;

		[Range( 0.2f, 4f )] public float highCoverageContrast = 1.4f;
		[Tooltip( "Drift speed relative to the low coverage field." )]
		[Range( 0f, 3f )] public float highCoverageWindFactor = 1.4f;

		[Tooltip( "Feature size of the field that selects the ice genus: fibrous Cirrus, granular " +
			"Cirrocumulus or a Cirrostratus veil." )]
		[Range( 0.1f, 6f )] public float highFormScale = 0.9f;
		[Range( -0.5f, 0.5f )] public float highFormBias = 0f;
		[Range( 0f, 3f )] public float highFormWindFactor = 1.1f;

		[Header( "Shell" )]
		[Tooltip( "Sea level, in world units. The clouds have their own value, because the " +
			"atmosphere's planetRadius is zero when the atmosphere is off." )]
		public float bodyRadius = 150f;

		[Tooltip( "Cloud base above sea level, in world units. One unit is about 0.91 km." )]
		public float cloudBottomAltitude = 1f;
		public float cloudTopAltitude = 10f;

		[Header( "Density" )]
		[Range( 0.001f, 0.5f )] public float shapeScale = 0.06f;
		[Range( 0.01f, 4f )] public float detailScale = 0.5f;
		[Range( 0f, 1f )] public float detailWeight = 0.35f;
		[Range( 0f, 8f )] public float densityMultiplier = 1.5f;
		[Range( 0f, 3f )] public float coverageMultiplier = 1f;
		[Range( -0.5f, 0.5f )] public float typeOffset = 0f;

		[Tooltip( "How far the cloud band moves up and down across the globe, as a fraction of the shell." )]
		[Range( 0f, 0.6f )] public float baseVariation = 0.3f;

		[Tooltip( "Thickness of the cloud band, as a fraction of the shell. Keep base variation plus " +
			"this value below 1." )]
		[Range( 0.1f, 1f )] public float bandThickness = 0.65f;

		[Tooltip( "How much of the height gradient acts on coverage instead of density. At 0 the layer " +
			"ends at a flat surface, as in Schneider. Higher values thin the top and bottom into " +
			"scattered cloud." )]
		[Range( 0f, 1f )] public float edgeSoftness = 0.7f;

		[Tooltip( "Vertical profile per cloud type, as fractions of the band: rise start, rise end, " +
			"fall start, fall end. After Schneider's height gradients, with authored values." )]
		public Vector4 stratusProfile = new Vector4( 0f, 0.05f, 0.12f, 0.22f );
		public Vector4 cumulusProfile = new Vector4( 0f, 0.18f, 0.55f, 0.85f );
		public Vector4 cumulonimbusProfile = new Vector4( 0f, 0.10f, 0.85f, 1f );

		[Header( "High band - the ice layer" )]
		[Tooltip( "Strength of the ice layer: Cirrus, Cirrocumulus and Cirrostratus. 0 removes the layer." )]
		[Range( 0f, 1f )] public float highStrength = 1f;

		[Tooltip( "Floor of the ice layer, as a fraction of the shell. The default puts it at about " +
			"6000 m, where high cloud starts." )]
		[Range( 0f, 0.95f )] public float highBase = 0.64f;

		[Tooltip( "Thickness of the ice layer, as a fraction of the shell. A thick layer looks like high fog." )]
		[Range( 0.02f, 0.4f )] public float highThickness = 0.22f;

		[Tooltip( "Vertical profile of the ice layer, as fractions of its thickness: rise start, rise " +
			"end, fall start, fall end." )]
		public Vector4 highProfile = new Vector4( 0f, 0.35f, 0.55f, 1f );

		[Range( 0.001f, 0.5f )] public float highScale = 0.05f;

		[Tooltip( "Much lower than the low layer's value, because cirrus is thin enough to show the sun " +
			"through it." )]
		[Range( 0f, 2f )] public float highDensityMultiplier = 0.28f;

		[Range( 0f, 3f )] public float highCoverageMultiplier = 1f;

		[Tooltip( "How far the fibrous form stretches the sample along the wind. The streaks make " +
			"cirrus look like ice, not like thin cumulus." )]
		[Range( 1f, 16f )] public float highStretch = 7f;

		[Tooltip( "How much the granular form uses the cellular noise channels. Cirrocumulus is a " +
			"field of small cells." )]
		[Range( 0f, 1f )] public float highGranularity = 0.8f;

		[Tooltip( "Direction of the fibres and of the ice layer's drift. High cloud has its own wind." )]
		public Vector3 highWindDirection = new Vector3( 1f, 0f, 0.35f );
		public float highWindSpeed = 0.05f;

		[Header( "Precipitation" )]
		[Tooltip( "Cloud with rain is thicker, taller and lower. One field controls all three." )]
		[Range( 0f, 3f )] public float precipDensity = 1.2f;
		[Range( 0f, 1f )] public float precipTypePush = 0.45f;
		[Range( 0f, 0.6f )] public float precipBaseDrop = 0.2f;

		[Header( "Wind" )]
		public Vector3 shapeWindDirection = new Vector3( 1f, 0.1f, 0.2f );
		public float shapeWindSpeed = 0.02f;
		public float detailWindSpeed = 0.05f;

		[Header( "March" )]
		[Tooltip( "Target length of one march step, in world units. The step count follows from the " +
			"length of the segment." )]
		[Range( 0.005f, 1f )] public float stepSize = 0.05f;

		[Range( 16, 512 )] public int maxSteps = 192;

		[Tooltip( "Diagnostic view in place of the clouds. Coverage to Base Height show the weather map " +
			"where the ray enters the shell. Camera Region keeps the clouds and shows where the camera " +
			"is relative to the shell, in a corner." )]
		public DebugMode debugMode = DebugMode.Off;
		[Range( 0f, 2f )] public float jitterStrength = 1f;
		[Range( 0.1f, 20f )] public float extinction = 4f;

		[Header( "Lighting" )]
		[Tooltip( "Scales the direct sunlight. The colour comes from the transmittance LUT. The level " +
			"is authored, because the march works in display space." )]
		[Range( 0f, 8f )] public float sunIntensity = 2.2f;

		[Tooltip( "How much of the atmosphere's extinction the sunlight takes. This atmosphere is much " +
			"thicker than Earth's, so at 1 the cloud tops look grey." )]
		[FormerlySerializedAs( "sunTransmittanceWeight" )]
		[Range( 0f, 1f )] public float lightTransmittanceWeight = 0.7f;

		[Tooltip( "Scales the skylight on the clouds. In daylight, keep it well below the direct " +
			"sunlight, or the clouds look flat." )]
		[Range( 0f, 4f )] public float ambientIntensity = 0.4f;

		[Tooltip( "How much skylight comes from the horizon below the sun instead of the zenith. This " +
			"gives the clouds the colour of the sunset." )]
		[Range( 0f, 1f )] public float ambientHorizon = 0.6f;

		[Tooltip( "Skylight to use when no physically based atmosphere is active." )]
		public Color ambientFallback = new Color( 0.4f, 0.45f, 0.55f );

		[Tooltip( "How much haze the clouds take from the air between them and the camera. 0 turns it off." )]
		[Range( 0f, 1f )] public float aerialStrength = 1f;

		[Tooltip( "Starlight and airglow, so the clouds do not go black at night. Not physical." )]
		public Color nightAmbient = new Color( 0.03f, 0.035f, 0.05f );

		[Header( "Light march" )]
		[Tooltip( "How far the light march reaches toward the sun, in world units. About one shell " +
			"thickness is usually correct." )]
		[Range( 0.1f, 8f )] public float lightMarchLength = 1.6f;
		[Range( 0.1f, 8f )] public float lightAbsorption = 2.5f;
		[Tooltip( "How wide the light march cone is. A wider cone lets light wrap around a billow." )]
		[Range( 0f, 1f )] public float coneSpread = 0.25f;
		[Tooltip( "Distance of the sixth light sample, as a multiple of the light march length. It " +
			"catches shadows from distant clouds, as in Schneider." )]
		[Range( 1f, 8f )] public float lightFarDistance = 3f;

		[Tooltip( "How fine a detail the light march reads, in world units. Lower values give sharper " +
			"self-shadowing. Raise it if the shadowing looks noisy." )]
		[Range( 0.02f, 2f )] public float lightMarchDetail = 0.15f;
		[Tooltip( "Dark edge where light enters a thin part of the cloud. Above about 0.45, the cloud " +
			"tops become darker than the bases." )]
		[Range( 0f, 1f )] public float powderStrength = 0.25f;

		[Header( "Phase" )]
		[Tooltip( "Forward lobe of the phase function. High values make the glow around the sun much " +
			"brighter than the sides of the cloud." )]
		[Range( 0f, 0.99f )] public float phaseForward = 0.45f;
		[Range( 0f, 0.99f )] public float phaseBackward = 0.2f;
		[Range( 0f, 1f )] public float phaseBlend = 0.5f;

		[Tooltip( "Strength of the silver lining when looking at the sun, relative to isotropic " +
			"scattering. 0 removes it." )]
		[Range( 0f, 24f )] public float silverIntensity = 6f;

		[Tooltip( "How wide the silver lining is. 0.05 gives a rim of about 4 degrees around the sun, " +
			"0.25 about 13, and 0.3 about 22." )]
		[Range( 0.02f, 0.6f )] public float silverSpread = 0.25f;

		[Header( "Moon" )]
		[Tooltip( "Scales moonlight on the clouds. The moon's colour includes its phase, so a new moon " +
			"adds nothing. 0 turns it off." )]
		[Range( 0f, 8f )] public float moonIntensity = 2f;

		[Tooltip( "Silver lining from the moon. It makes a cloud edge in front of the moon look lit." )]
		[Range( 0f, 24f )] public float moonSilverIntensity = 2f;

		[Header( "Sky under the deck" )]
		[Tooltip( "Bakes how the cloud base looks from below and how much sky it hides. The ocean and " +
			"the terrain use it for their skylight and reflections." )]
		public ComputeShader skyCompute;

		[Tooltip( "Smaller than the shadow map, because each step of this pass does a full light " +
			"march. The cloud base has little fine detail." )]
		public Vector2Int skyMapSize = new Vector2Int( 256, 128 );

		[Range( 2, 32 )] public int skySteps = 8;

		[Tooltip( "Widens the footprint of each density sample, relative to the map resolution. Lower " +
			"values alias, and the wind makes the aliasing move." )]
		[Range( 0.5f, 4f )] public float skyDetailScale = 1f;

		[Tooltip( "How much cloud replaces the sky that the surfaces see. 0 turns it off." )]
		[Range( 0f, 1f )] public float skyStrength = 1f;

		[Header( "Genus override" )]
		[Tooltip( "Sets all weather fields to one genus, over the whole globe. It uses the same density " +
			"model, so it shows what the renderer can produce." )]
		public Genus genusOverride = Genus.Off;

		[Header( "Cost" )]
		[Tooltip( "Full marches every pixel. Half marches a quarter of the pixels and upsamples. " +
			"Temporal marches a quarter of the pixels each frame and reprojects the rest." )]
		public CostMode costMode = CostMode.Half;

		[Tooltip( "How strongly the upsample ignores neighbours across a depth edge. Higher values keep " +
			"edges sharp but show more stair-steps." )]
		[Range( 0f, 64f )] public float depthRejection = 8f;

		[Tooltip( "Temporal only. How much of the new frame to use. Lower values are steadier, but a " +
			"fast camera move leaves a trail." )]
		[Range( 0.05f, 1f )] public float historyBlend = 0.2f;

		[Header( "Shadows on the ground" )]
		public ComputeShader shadowCompute;
		[Tooltip( "Equirectangular, so it does not change with the camera. A low resolution is enough " +
			"for shadows from high cloud." )]
		public Vector2Int shadowMapSize = new Vector2Int( 512, 256 );
		[Range( 2, 32 )] public int shadowSteps = 10;
		[Range( 0.1f, 8f )] public float shadowAbsorption = 1.2f;

		[Tooltip( "Widens the footprint of each shadow sample, relative to the map resolution. 1 stops " +
			"the flicker. Higher values give softer shadows." )]
		[Range( 0.5f, 4f )] public float shadowDetailScale = 1f;
		[Tooltip( "Strength of the cloud shadows on the ground. 0 turns them off." )]
		[Range( 0f, 1f )] public float shadowStrength = 0.85f;

		[Tooltip( "How strongly the clouds shadow the air, which gives light shafts. It costs much more " +
			"than the ground shadows, so it has its own control. 0 turns it off." )]
		[Range( 0f, 1f )] public float shaftStrength = 1f;

		const int TemporalPeriod = 4;

		RenderTexture weatherMap;
		RenderTexture weatherMapHigh;
		RenderTexture shadowMap;
		RenderTexture skyMap;

		CachedKernel weatherKernel;
		CachedKernel shadowKernel;
		CachedKernel skyMapKernel;

		// Exact signatures of each map's inputs, so a dispatch that would reproduce the texture is skipped
		int lastWeatherSignature;
		int lastShadowSignature;
		int lastSkySignature;

		bool publishedShadows;
		bool publishedSky;

		readonly RenderTexture[] history = new RenderTexture[ 2 ];
		int historyIndex;
		bool historyValid;
		int temporalFrame;
		Matrix4x4 previousViewProjection = Matrix4x4.identity;

		float elapsed;
		int lastAdvanceFrame = -1;

		Light sunLight;
		bool sunTagMissing;

		SolarSystem.SolarSystemManager solarSystem;

		///////// Public API /////////////

		public float InnerRadius => bodyRadius + cloudBottomAltitude;
		public float OuterRadius => bodyRadius + Mathf.Max( cloudBottomAltitude + 0.01f, cloudTopAltitude );

		public bool ApplyDensityValuesTo( ComputeShader compute, int kernel )
		{
			if ( !HasInputs )
			{
				return false;
			}

			RenderWeatherMap();
			BindDensity( new ComputeSink( compute, kernel ) );
			return true;
		}

		///////// Lifecycle /////////////

		public override void OnEnable()
		{
			if ( shader == null )
			{
				shader = Shader.Find( "Hidden/Clouds" );
			}

			base.OnEnable();

			Camera.onPreCull -= RenderMaps;
			Camera.onPreCull += RenderMaps;
		}

		public override void OnDestroy()
		{
			Camera.onPreCull -= RenderMaps;
			Release( ref weatherMap );
			Release( ref weatherMapHigh );
			Release( ref shadowMap );
			Release( ref skyMap );
			ReleaseHistory();
		}

		///////// Global maps, at pre-cull /////////////

		bool HasInputs => shapeNoise != null && detailNoise != null && weatherCompute != null;

		float MidShellRadius => ( InnerRadius + OuterRadius ) * 0.5f;

		void RenderMaps( Camera renderingCamera )
		{
			if ( renderingCamera != cam )
			{
				return;
			}

			if ( !enabled || !HasInputs )
			{
				RetractShadows();
				RetractSky();
				return;
			}

			RenderWeatherMap();
			RenderShadowMap();
			RenderSkyMap();
		}

		void Advance()
		{
			if ( Time.frameCount == lastAdvanceFrame )
			{
				return;
			}

			lastAdvanceFrame = Time.frameCount;

			if ( solarSystem == null )
			{
				solarSystem = FindFirstObjectByType<SolarSystem.SolarSystemManager>();
			}

			float timeScale = solarSystem == null ? 1f : ( solarSystem.animate ? solarSystem.timeMultiplier : 0f );
			elapsed += Time.deltaTime * timeScale;
		}

		void RenderWeatherMap()
		{
			int width = Mathf.Max( 8, weatherMapSize.x );
			int height = Mathf.Max( 8, weatherMapSize.y );

			bool created = EnsureGlobeMap( ref weatherMap, width, height, GraphicsFormat.R8G8B8A8_UNorm, "Cloud Weather Map" );
			created |= EnsureGlobeMap( ref weatherMapHigh, width, height, GraphicsFormat.R8G8B8A8_UNorm, "Cloud Weather Map High" );

			Advance();
			float weatherOffset = elapsed * weatherWindSpeed;

			int signature = WeatherSignature( width, height, weatherOffset );
			if ( !created && signature == lastWeatherSignature )
			{
				return;
			}

			lastWeatherSignature = signature;

			int kernel = weatherKernel.Of( weatherCompute, "CSWeatherMap" );
			weatherCompute.SetTexture( kernel, "Result", weatherMap );
			weatherCompute.SetTexture( kernel, "ResultHigh", weatherMapHigh );
			weatherCompute.SetInt( "octaves", weatherOctaves );
			weatherCompute.SetFloat( "lacunarity", weatherLacunarity );
			weatherCompute.SetFloat( "gain", weatherGain );
			weatherCompute.SetFloat( "weatherRangeGain", weatherRangeGain );
			weatherCompute.SetFloat( "weatherSeed", weatherSeed );
			weatherCompute.SetVector( "windOffset", WeatherWindDirection * weatherOffset );

			weatherCompute.SetFloat( "coverageScale", coverageScale );
			weatherCompute.SetFloat( "coverageBias", coverageBias );
			weatherCompute.SetFloat( "coverageContrast", coverageContrast );
			weatherCompute.SetFloat( "typeScale", typeScale );
			weatherCompute.SetFloat( "typeBias", typeBias );
			weatherCompute.SetFloat( "typeContrast", typeContrast );
			weatherCompute.SetFloat( "typeWindFactor", typeWindFactor );
			weatherCompute.SetFloat( "precipitationScale", precipitationScale );
			weatherCompute.SetFloat( "precipitationBias", precipitationBias );
			weatherCompute.SetFloat( "precipitationWindFactor", precipitationWindFactor );
			weatherCompute.SetFloat( "baseHeightScale", baseHeightScale );
			weatherCompute.SetFloat( "baseHeightBias", baseHeightBias );
			weatherCompute.SetFloat( "baseHeightWindFactor", baseHeightWindFactor );

			weatherCompute.SetFloat( "highCoverageScale", highCoverageScale );
			weatherCompute.SetFloat( "highCoverageBias", highCoverageBias );
			weatherCompute.SetFloat( "highCoverageContrast", highCoverageContrast );
			weatherCompute.SetFloat( "highCoverageWindFactor", highCoverageWindFactor );
			weatherCompute.SetFloat( "highFormScale", highFormScale );
			weatherCompute.SetFloat( "highFormBias", highFormBias );
			weatherCompute.SetFloat( "highFormWindFactor", highFormWindFactor );

			weatherKernel.Dispatch( width, height );
		}

		void RenderShadowMap()
		{
			if ( shadowCompute == null || shadowStrength <= 0f )
			{
				RetractShadows();
				return;
			}

			int width = Mathf.Max( 8, shadowMapSize.x );
			int height = Mathf.Max( 8, shadowMapSize.y );

			bool created = EnsureGlobeMap( ref shadowMap, width, height, GraphicsFormat.R8G8B8A8_UNorm, "Cloud Shadow Map" );

			Vector3 sunDir = SunDirection();

			int signature = ShadowSignature( width, height, sunDir );
			if ( created || signature != lastShadowSignature )
			{
				lastShadowSignature = signature;

				int kernel = shadowKernel.Of( shadowCompute, "CSCloudShadow" );
				BindDensity( new ComputeSink( shadowCompute, kernel ) );
				shadowCompute.SetTexture( kernel, "Result", shadowMap );
				shadowCompute.SetVector( "shadowSunDir", sunDir );
				shadowCompute.SetFloat( "shadowGroundRadius", bodyRadius );
				shadowCompute.SetInt( "shadowSteps", shadowSteps );
				shadowCompute.SetFloat( "shadowAbsorption", shadowAbsorption );
				shadowCompute.SetFloat( "shadowDetailScale", shadowDetailScale );

				shadowKernel.Dispatch( width, height );
			}

			CloudShadowGlobals.Publish( shadowMap, shadowStrength, shaftStrength, MidShellRadius );
			publishedShadows = true;
		}

		void RenderSkyMap()
		{
			if ( skyCompute == null || skyStrength <= 0f )
			{
				RetractSky();
				return;
			}

			int width = Mathf.Max( 8, skyMapSize.x );
			int height = Mathf.Max( 8, skyMapSize.y );

			bool created = EnsureGlobeMap( ref skyMap, width, height, GraphicsFormat.R16G16B16A16_SFloat, "Cloud Sky Map" );

			Vector3 sunDir = SunDirection();

			int signature = SkySignature( width, height, sunDir );
			if ( created || signature != lastSkySignature )
			{
				lastSkySignature = signature;

				int kernel = skyMapKernel.Of( skyCompute, "CSCloudSkyMap" );
				var sink = new ComputeSink( skyCompute, kernel );
				BindDensity( sink );
				BindLighting( sink, sunDir );

				CloudShadowGlobals.BindTo( skyCompute, kernel );
				BindAtmosphereLUTs( skyCompute, kernel );
				skyCompute.SetVector( "moonPosition", Shader.GetGlobalVector( "moonPosition" ) );
				skyCompute.SetVector( "moonLightColour", Shader.GetGlobalVector( "moonLightColour" ) );

				skyCompute.SetTexture( kernel, "Result", skyMap );
				skyCompute.SetInt( "cloudSkySteps", skySteps );
				skyCompute.SetFloat( "cloudSkyDetailScale", skyDetailScale );

				skyMapKernel.Dispatch( width, height );
			}

			Shader.SetGlobalTexture( "CloudSkyMap", skyMap );
			Shader.SetGlobalFloat( "cloudSkyStrength", skyStrength );
			CloudShadowGlobals.PublishDeckRadius( MidShellRadius );
			publishedSky = true;
		}

		void RetractShadows()
		{
			if ( !publishedShadows )
			{
				return;
			}
			publishedShadows = false;
			CloudShadowGlobals.Retract();
		}

		void RetractSky()
		{
			if ( !publishedSky )
			{
				return;
			}
			publishedSky = false;
			Shader.SetGlobalFloat( "cloudSkyStrength", 0f );
		}

		static void BindAtmosphereLUTs( ComputeShader compute, int kernel )
		{
			Texture transmittanceLUT = Shader.GetGlobalTexture( "TransmittanceLUT" );
			if ( transmittanceLUT != null )
			{
				compute.SetTexture( kernel, "TransmittanceLUT", transmittanceLUT );
			}

			Texture skyViewLUT = Shader.GetGlobalTexture( "SkyViewLUT" );
			if ( skyViewLUT != null )
			{
				compute.SetTexture( kernel, "SkyViewLUT", skyViewLUT );
			}
		}

		///////// The march, in the post-process chain /////////////

		protected override void RenderEffectToTarget( RenderTexture source, RenderTexture target )
		{
			if ( !HasInputs || weatherMap == null )
			{
				// Nothing baked, wired or generated yet: pass the frame through untouched
				Graphics.Blit( source, target );
				return;
			}

			BindMarch();

			int divisor = costMode == CostMode.Half ? 2 : 1;
			int width = Mathf.Max( 1, source.width / divisor );
			int height = Mathf.Max( 1, source.height / divisor );
			var marchSize = new Vector4( width, height, 1f / width, 1f / height );

			material.SetVector( "_CloudMarchSize", marchSize );
			material.SetInt( "cloudTemporalPeriod", costMode == CostMode.Temporal ? TemporalPeriod : 1 );
			material.SetInt( "cloudTemporalIndex", temporalFrame % TemporalPeriod );

			RenderTexture cloudTex = RenderTexture.GetTemporary( width, height, 0, RenderTextureFormat.ARGBHalf );
			cloudTex.filterMode = FilterMode.Bilinear;

			Graphics.Blit( source, cloudTex, material, 0 );

			RenderTexture resolved = cloudTex;

			if ( costMode == CostMode.Temporal )
			{
				EnsureHistory( width, height );
				int previous = historyIndex;
				int current = 1 - historyIndex;

				material.SetTexture( "_CloudTex", cloudTex );
				material.SetTexture( "_CloudHistory", history[ previous ] );
				material.SetMatrix( "_CloudPrevViewProj", previousViewProjection );
				material.SetFloat( "_CloudHistoryBlend", historyBlend );
				material.SetFloat( "_CloudHistoryValid", historyValid ? 1f : 0f );
				material.SetFloat( "_CloudReprojectRadius", MidShellRadius );

				Graphics.Blit( source, history[ current ], material, 1 );

				resolved = history[ current ];
				historyIndex = current;
				historyValid = true;
			}
			else
			{
				historyValid = false;
			}

			material.SetTexture( "_CloudTex", resolved );
			material.SetVector( "_CloudTexSize", marchSize );
			material.SetFloat( "_CloudUpsample", divisor );
			material.SetFloat( "_CloudDepthRejection", depthRejection );

			Graphics.Blit( source, target, material, 2 );

			RenderTexture.ReleaseTemporary( cloudTex );

			if ( cam != null )
			{
				previousViewProjection = cam.projectionMatrix * cam.worldToCameraMatrix;
			}
			temporalFrame++;
		}

		void BindMarch()
		{
			var sink = new MaterialSink( material );
			BindDensity( sink );
			BindLighting( sink, SunDirection() );

			material.SetFloat( "cloudAerialStrength", aerialStrength );
			material.SetFloat( "cloudStepSize", stepSize );
			material.SetInt( "cloudMaxSteps", maxSteps );
			material.SetFloat( "cloudJitterStrength", jitterStrength );

			material.SetInt( "cloudDebugMode", debugMode == DebugMode.CameraRegion ? 0 : (int)debugMode );
			float region = -1f;
			if ( debugMode == DebugMode.CameraRegion && cam != null )
			{
				float camRadius = cam.transform.position.magnitude;
				region = camRadius > OuterRadius ? 0f : ( camRadius > InnerRadius ? 1f : 2f );
			}
			material.SetFloat( "_CloudDebugRegion", region );
		}

		void EnsureHistory( int width, int height )
		{
			if ( history[ 0 ] != null && history[ 0 ].width == width && history[ 0 ].height == height )
			{
				return;
			}

			ReleaseHistory();
			for ( int i = 0; i < history.Length; i++ )
			{
				history[ i ] = new RenderTexture( width, height, 0, RenderTextureFormat.ARGBHalf )
				{
					filterMode = FilterMode.Bilinear,
					wrapMode = TextureWrapMode.Clamp,
					name = $"Cloud History {i}"
				};
				history[ i ].Create();
			}
		}

		void ReleaseHistory()
		{
			for ( int i = 0; i < history.Length; i++ )
			{
				Release( ref history[ i ] );
			}
			historyValid = false;
		}

		///////// Shared bindings /////////////

		interface IUniformSink
		{
			void SetFloat( string name, float value );
			void SetInt( string name, int value );
			void SetVector( string name, Vector4 value );
			void SetTexture( string name, Texture value );
		}

		readonly struct MaterialSink : IUniformSink
		{
			readonly Material material;
			public MaterialSink( Material material ) { this.material = material; }
			public void SetFloat( string name, float value ) => material.SetFloat( name, value );
			public void SetInt( string name, int value ) => material.SetInt( name, value );
			public void SetVector( string name, Vector4 value ) => material.SetVector( name, value );
			public void SetTexture( string name, Texture value ) => material.SetTexture( name, value );
		}

		readonly struct ComputeSink : IUniformSink
		{
			readonly ComputeShader compute;
			readonly int kernel;
			public ComputeSink( ComputeShader compute, int kernel ) { this.compute = compute; this.kernel = kernel; }
			public void SetFloat( string name, float value ) => compute.SetFloat( name, value );
			public void SetInt( string name, int value ) => compute.SetInt( name, value );
			public void SetVector( string name, Vector4 value ) => compute.SetVector( name, value );
			public void SetTexture( string name, Texture value ) => compute.SetTexture( kernel, name, value );
		}

		void BindDensity<T>( T sink ) where T : struct, IUniformSink
		{
			sink.SetTexture( "CloudShapeNoise", shapeNoise );
			sink.SetTexture( "CloudDetailNoise", detailNoise );
			sink.SetTexture( "CloudWeatherMapTex", weatherMap );
			sink.SetTexture( "CloudWeatherMapHighTex", weatherMapHigh );

			sink.SetFloat( "cloudInnerRadius", InnerRadius );
			sink.SetFloat( "cloudOuterRadius", OuterRadius );
			sink.SetFloat( "cloudShapeScale", shapeScale );
			sink.SetFloat( "cloudDetailScale", detailScale );
			sink.SetFloat( "cloudDetailWeight", detailWeight );
			sink.SetFloat( "cloudDensityMultiplier", densityMultiplier );
			sink.SetFloat( "cloudCoverageMultiplier", coverageMultiplier );
			sink.SetFloat( "cloudTypeBias", typeOffset );
			sink.SetFloat( "cloudShapeResolution", shapeNoise != null ? shapeNoise.width : 128 );
			sink.SetFloat( "cloudDetailResolution", detailNoise != null ? detailNoise.width : 32 );
			sink.SetFloat( "cloudBaseVariation", baseVariation );
			sink.SetFloat( "cloudBandThickness", bandThickness );
			sink.SetFloat( "cloudEdgeSoftness", edgeSoftness );
			sink.SetVector( "cloudStratusProfile", stratusProfile );
			sink.SetVector( "cloudCumulusProfile", cumulusProfile );
			sink.SetVector( "cloudCumulonimbusProfile", cumulonimbusProfile );
			sink.SetFloat( "cloudPrecipDensity", precipDensity );
			sink.SetFloat( "cloudPrecipTypePush", precipTypePush );
			sink.SetFloat( "cloudPrecipBaseDrop", precipBaseDrop );
			sink.SetVector( "cloudShapeWind", ShapeWind );
			sink.SetVector( "cloudDetailWind", DetailWind );

			sink.SetFloat( "cloudHighStrength", highStrength );
			sink.SetFloat( "cloudHighBase", highBase );
			sink.SetFloat( "cloudHighThickness", highThickness );
			sink.SetVector( "cloudHighProfile", highProfile );
			sink.SetFloat( "cloudHighScale", highScale );
			sink.SetFloat( "cloudHighDensityMultiplier", highDensityMultiplier );
			sink.SetFloat( "cloudHighCoverageMultiplier", highCoverageMultiplier );
			sink.SetFloat( "cloudHighStretch", highStretch );
			sink.SetFloat( "cloudHighGranularity", highGranularity );
			sink.SetVector( "cloudHighWindDir", HighWindDir );
			sink.SetVector( "cloudHighWind", HighWind );

			GenusSettings( genusOverride, out Vector4 low, out Vector2 high, out float baseOffset );
			sink.SetFloat( "cloudWeatherOverrideOn", genusOverride == Genus.Off ? 0f : 1f );
			sink.SetVector( "cloudWeatherOverride", low );
			sink.SetVector( "cloudHighWeatherOverride", high );
			sink.SetFloat( "cloudGenusBaseOffset", baseOffset );
		}

		void BindLighting<T>( T sink, Vector3 sunDir ) where T : struct, IUniformSink
		{
			sink.SetVector( "cloudSunDir", sunDir );
			sink.SetVector( "cloudSunColour", Opaque( SunGradient() ) );
			sink.SetFloat( "cloudSunIntensity", sunIntensity );
			sink.SetFloat( "cloudLightTransmittanceWeight", lightTransmittanceWeight );
			sink.SetFloat( "cloudAmbientIntensity", ambientIntensity );
			sink.SetFloat( "cloudAmbientHorizon", ambientHorizon );
			sink.SetVector( "cloudAmbientFallback", Opaque( ambientFallback ) );
			sink.SetVector( "cloudNightAmbient", Opaque( nightAmbient ) );
			sink.SetFloat( "cloudExtinction", extinction );
			sink.SetFloat( "cloudMoonIntensity", moonIntensity );
			sink.SetFloat( "cloudMoonSilverIntensity", moonSilverIntensity );

			sink.SetFloat( "cloudLightMarchLength", lightMarchLength );
			sink.SetFloat( "cloudLightAbsorption", lightAbsorption );
			sink.SetFloat( "cloudConeSpread", coneSpread );
			sink.SetFloat( "cloudLightFarDistance", lightFarDistance );
			sink.SetFloat( "cloudLightMarchDetail", lightMarchDetail );
			sink.SetFloat( "cloudPowderStrength", powderStrength );

			sink.SetFloat( "cloudPhaseForward", phaseForward );
			sink.SetFloat( "cloudPhaseBackward", phaseBackward );
			sink.SetFloat( "cloudPhaseBlend", phaseBlend );
			sink.SetFloat( "cloudSilverIntensity", silverIntensity );
			sink.SetFloat( "cloudSilverSpread", silverSpread );
		}

		static Vector4 Opaque( Color colour ) => new Vector4( colour.r, colour.g, colour.b, 1 );

		///////// Signatures /////////////

		static readonly Vector3 WeatherWindDirection = new Vector3( 1f, 0.3f, 0.7f );

		int WeatherSignature( int width, int height, float weatherOffset )
		{
			var h = new System.HashCode();
			h.Add( width ); h.Add( height ); h.Add( weatherOffset );
			h.Add( weatherOctaves ); h.Add( weatherLacunarity ); h.Add( weatherGain );
			h.Add( weatherRangeGain ); h.Add( weatherSeed );
			h.Add( coverageScale ); h.Add( coverageBias ); h.Add( coverageContrast );
			h.Add( typeScale ); h.Add( typeBias ); h.Add( typeContrast ); h.Add( typeWindFactor );
			h.Add( precipitationScale ); h.Add( precipitationBias ); h.Add( precipitationWindFactor );
			h.Add( baseHeightScale ); h.Add( baseHeightBias ); h.Add( baseHeightWindFactor );
			h.Add( highCoverageScale ); h.Add( highCoverageBias ); h.Add( highCoverageContrast ); h.Add( highCoverageWindFactor );
			h.Add( highFormScale ); h.Add( highFormBias ); h.Add( highFormWindFactor );
			return h.ToHashCode();
		}

		int DensitySignature()
		{
			var h = new System.HashCode();
			h.Add( lastWeatherSignature );
			h.Add( shapeNoise != null ? shapeNoise.GetInstanceID() : 0 );
			h.Add( shapeNoise != null ? shapeNoise.width : 0 );
			h.Add( ContentsHash( shapeNoise ) );
			h.Add( detailNoise != null ? detailNoise.GetInstanceID() : 0 );
			h.Add( detailNoise != null ? detailNoise.width : 0 );
			h.Add( ContentsHash( detailNoise ) );
			h.Add( InnerRadius ); h.Add( OuterRadius );
			h.Add( shapeScale ); h.Add( detailScale ); h.Add( detailWeight );
			h.Add( densityMultiplier ); h.Add( coverageMultiplier ); h.Add( typeOffset );
			h.Add( baseVariation ); h.Add( bandThickness ); h.Add( edgeSoftness );
			h.Add( stratusProfile ); h.Add( cumulusProfile ); h.Add( cumulonimbusProfile );
			h.Add( precipDensity ); h.Add( precipTypePush ); h.Add( precipBaseDrop );
			h.Add( elapsed );
			h.Add( shapeWindDirection ); h.Add( shapeWindSpeed ); h.Add( detailWindSpeed );
			h.Add( highWindDirection ); h.Add( highWindSpeed );
			h.Add( highStrength ); h.Add( highBase ); h.Add( highThickness ); h.Add( highProfile ); h.Add( highScale );
			h.Add( highDensityMultiplier ); h.Add( highCoverageMultiplier );
			h.Add( highStretch ); h.Add( highGranularity );
			h.Add( genusOverride );
			return h.ToHashCode();
		}

		static int ContentsHash( Texture3D texture )
		{
#if UNITY_EDITOR
			return texture != null ? texture.imageContentsHash.GetHashCode() : 0;
#else
			return 0;
#endif
		}

		int ShadowSignature( int width, int height, Vector3 sunDir )
		{
			var h = new System.HashCode();
			h.Add( DensitySignature() );
			h.Add( width ); h.Add( height ); h.Add( sunDir ); h.Add( bodyRadius );
			h.Add( shadowSteps ); h.Add( shadowAbsorption ); h.Add( shadowDetailScale );
			return h.ToHashCode();
		}

		int SkySignature( int width, int height, Vector3 sunDir )
		{
			var h = new System.HashCode();
			h.Add( DensitySignature() );
			h.Add( width ); h.Add( height ); h.Add( sunDir );
			h.Add( skySteps ); h.Add( skyDetailScale );
			h.Add( cam != null ? cam.transform.position : Vector3.zero );
			h.Add( Shader.GetGlobalFloat( "atmosphereSettingsVersion" ) );
			h.Add( Shader.GetGlobalFloat( "skyReflectionStrength" ) );
			h.Add( Shader.GetGlobalVector( "moonPosition" ) ); h.Add( Shader.GetGlobalVector( "moonLightColour" ) );
			// BindLighting
			h.Add( sunLight != null ? sunLight.color : Color.white );
			h.Add( sunIntensity ); h.Add( lightTransmittanceWeight );
			h.Add( ambientIntensity ); h.Add( ambientHorizon );
			h.Add( ambientFallback ); h.Add( nightAmbient ); h.Add( extinction );
			h.Add( moonIntensity ); h.Add( moonSilverIntensity );
			h.Add( lightMarchLength ); h.Add( lightAbsorption ); h.Add( coneSpread ); h.Add( lightFarDistance );
			h.Add( lightMarchDetail ); h.Add( powderStrength );
			h.Add( phaseForward ); h.Add( phaseBackward ); h.Add( phaseBlend );
			h.Add( silverIntensity ); h.Add( silverSpread );
			return h.ToHashCode();
		}

		///////// Genus override /////////////

		static void GenusSettings( Genus genus, out Vector4 low, out Vector2 high, out float baseOffset )
		{
			switch ( genus )
			{
				// --- low band, floor on the deck -----------------------------------------------
				case Genus.Stratus:
					low = new Vector4( 0.95f, 0f, 0.00f, 0 ); high = Vector2.zero; baseOffset = 0.00f; return;
				case Genus.Stratocumulus:
					low = new Vector4( 0.80f, 0f, 0.28f, 0 ); high = Vector2.zero; baseOffset = 0.02f; return;
				case Genus.Cumulus:
					low = new Vector4( 0.50f, 0f, 0.50f, 0 ); high = Vector2.zero; baseOffset = 0.03f; return;
				case Genus.Cumulonimbus:
					low = new Vector4( 0.80f, 0.90f, 1.00f, 0 ); high = Vector2.zero; baseOffset = 0.00f; return;

				// --- mid band: the same forms, floor raised ------------------------------------
				case Genus.Altostratus:
					low = new Vector4( 0.95f, 0f, 0.00f, 0 ); high = Vector2.zero; baseOffset = 0.32f; return;
				case Genus.Altocumulus:
					low = new Vector4( 0.70f, 0f, 0.32f, 0 ); high = Vector2.zero; baseOffset = 0.30f; return;
				case Genus.Nimbostratus:
					low = new Vector4( 1.00f, 0.75f, 0.10f, 0 ); high = Vector2.zero; baseOffset = 0.22f; return;

				// --- high band: the ice layer, low layer off ------------------------------------
				case Genus.Cirrus:
					low = Vector4.zero; high = new Vector2( 0.35f, 0.00f ); baseOffset = 0f; return;
				case Genus.Cirrocumulus:
					low = Vector4.zero; high = new Vector2( 0.55f, 0.50f ); baseOffset = 0f; return;
				case Genus.Cirrostratus:
					low = Vector4.zero; high = new Vector2( 0.70f, 1.00f ); baseOffset = 0f; return;

				default:
					low = Vector4.zero; high = Vector2.zero; baseOffset = 0f; return;
			}
		}

		///////// Helpers /////////////

		static Vector3 SafeDirection( Vector3 direction ) =>
			direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.right;

		Vector3 ShapeWind => SafeDirection( shapeWindDirection ) * ( elapsed * shapeWindSpeed );
		Vector3 DetailWind => SafeDirection( shapeWindDirection ) * ( elapsed * detailWindSpeed );
		Vector3 HighWindDir => SafeDirection( highWindDirection );
		Vector3 HighWind => HighWindDir * ( elapsed * highWindSpeed );

		Vector3 SunDirection()
		{
			ResolveSun();
			return sunLight != null ? -sunLight.transform.forward : Vector3.up;
		}

		Color SunGradient()
		{
			Color sunColour = sunLight != null ? sunLight.color : Color.white;
			return Color.Lerp( sunColour, Color.white, Shader.GetGlobalFloat( "skyReflectionStrength" ) );
		}

		void ResolveSun()
		{
			if ( sunLight != null || sunTagMissing )
			{
				return;
			}

			GameObject sunObject;
			try
			{
				sunObject = GameObject.FindGameObjectWithTag( "Sun" );
			}
			catch ( UnityException )
			{
				sunTagMissing = true;
				Debug.LogWarning( "CloudEffect: no \"Sun\" tag defined in this project, so the clouds " +
					"cannot resolve a sun direction. Assign the light manually on the effect asset." );
				return;
			}

			sunLight = sunObject != null ? sunObject.GetComponent<Light>() : null;
		}

		static bool EnsureGlobeMap( ref RenderTexture map, int width, int height, GraphicsFormat format, string name )
		{
			if ( map != null && map.IsCreated() && map.width == width && map.height == height )
			{
				return false;
			}

			Release( ref map );
			map = new RenderTexture( width, height, 0, format )
			{
				enableRandomWrite = true,
				wrapModeU = TextureWrapMode.Repeat,
				wrapModeV = TextureWrapMode.Clamp,
				filterMode = FilterMode.Bilinear,
				name = name
			};
			map.Create();
			return true;
		}

		static void Release( ref RenderTexture texture )
		{
			if ( texture == null )
			{
				return;
			}

			texture.Release();
			DestroyImmediate( texture );
			texture = null;
		}
	}
}
