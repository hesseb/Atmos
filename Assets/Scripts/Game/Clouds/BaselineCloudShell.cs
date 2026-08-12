using UnityEngine;
using Seb.Meshing;

namespace Clouds
{
	/// <summary>
	/// The baseline clouds delivered as drawn geometry: one alpha-blended sphere per deck.
	///
	/// The second of the two delivery mechanisms. The post-process is the controlled measurement -
	/// it shares the volumetric's pass shape exactly, so the two can be compared with everything but
	/// the shading held constant. This is what a game would actually ship, and the gap between them
	/// is worth reporting on its own: a post-process pays for every sky pixel it will discard, a
	/// mesh pays vertex cost and a transparent pass instead, and which wins depends on how much of
	/// the screen the clouds cover.
	///
	/// A MonoBehaviour rather than a ScriptableObject, for the reason BaselineSkyRenderer states:
	/// `enabled` on an effect asset is serialized state that reaches disk on a play-mode change, and
	/// the whole EffectStateGuard apparatus exists to contain that. A scene component sidesteps it.
	///
	/// Every shading parameter comes from the BaselineCloudEffect asset rather than being duplicated
	/// here, and the shader includes the same BaselineCloudCommon.hlsl. So the two deliveries cannot
	/// disagree about what a cloud looks like - which is what makes "these should differ in cost and
	/// not in image" a testable claim rather than a hope.
	/// </summary>
	[ExecuteInEditMode]
	public class BaselineCloudShell : MonoBehaviour
	{
		[Tooltip("The parameters. Shared with the post-process delivery deliberately - both read " +
			"this one asset, so neither can drift.")]
		public BaselineCloudEffect settings;

		public Shader shellShader;

		[Tooltip("Icosphere subdivisions. Only decides which pixels get shaded, never how: the " +
			"fragment intersects the analytic sphere regardless, so faceting cannot reach the " +
			"shading. Enough that the silhouette is smooth is enough.")]
		[Range(0, 128)] public int resolution = 48;

		[Tooltip("Scales the shell fractionally past its deck radius. An inscribed polyhedron's " +
			"silhouette sits inside the sphere it approximates, so without this a thin crescent is " +
			"clipped off the limb - which is exactly where a cloud deck is most visible.")]
		[Range(1f, 1.05f)] public float edgeInflate = 1.004f;

		[Tooltip("Layer the shells are drawn on. Must be one the main camera renders.")]
		public int drawLayer;

		Mesh sphere;
		Material lowerMaterial;
		Material upperMaterial;
		Camera cachedCamera;

		void OnDisable()
		{
			// The shells only exist while this is drawing them, so nothing to retract - but the
			// generated resources are ours to release.
			Release();
		}

		void OnDestroy() { Release(); }

		void Release()
		{
			if (sphere != null) { DestroyImmediate(sphere); sphere = null; }
			if (lowerMaterial != null) { DestroyImmediate(lowerMaterial); lowerMaterial = null; }
			if (upperMaterial != null) { DestroyImmediate(upperMaterial); upperMaterial = null; }
		}

		void LateUpdate()
		{
			if (settings == null || shellShader == null) { return; }
			if (settings.TextureFor(settings.lower) == null) { return; }

			EnsureResources();

			Camera drawCamera = ResolveCamera();
			if (drawCamera == null) { return; }

			// Pixel height of the frame, since the mip footprint follows the pass resolution. The
			// mesh always draws at full resolution - there is no half-res mode here, because
			// rendering geometry at half resolution and upsampling is a different technique rather
			// than the same one cheaper.
			settings.ApplyShadingValuesTo(lowerMaterial, drawCamera.pixelHeight);
			settings.ApplyShadingValuesTo(upperMaterial, drawCamera.pixelHeight);

			lowerMaterial.SetFloat("baselineShellIsUpper", 0f);
			upperMaterial.SetFloat("baselineShellIsUpper", 1f);

			float cameraRadius = drawCamera.transform.position.magnitude;
			float lowerRadius = settings.bodyRadius + settings.lower.altitude;
			float upperRadius = settings.bodyRadius + settings.upper.altitude;

			// Cull the face the camera is NOT on. Outside a deck the near hemisphere is the surface
			// you see; inside it, the far one is. This is the geometric counterpart of
			// baselineLayerHit taking the near hit from outside and the exit hit from inside, and
			// getting it wrong either draws the deck twice or not at all.
			lowerMaterial.SetFloat("_Cull", cameraRadius > lowerRadius ? 2f : 1f);   // Back : Front
			upperMaterial.SetFloat("_Cull", cameraRadius > upperRadius ? 2f : 1f);

			// Per-OBJECT sorting, and that is a real limitation of this delivery rather than an
			// oversight.
			//
			// Alpha blending needs back-to-front, and which deck is behind flips with the camera:
			// above both, the lower one is farther; below both, the upper one is. A queue can only
			// express one answer per frame, whereas the post-process sorts per pixel in
			// baselineCombineLayers. Near the horizon both orders genuinely occur in the same frame
			// and this gets one of them wrong.
			//
			// Worth recording as a finding: the drawn-mesh delivery cannot express a per-pixel sort,
			// so two overlapping transparent decks are a place where the cheap delivery is not
			// merely cheaper but less correct.
			bool lowerIsFarther = cameraRadius > upperRadius;
			lowerMaterial.renderQueue = lowerIsFarther ? 3000 : 3001;
			upperMaterial.renderQueue = lowerIsFarther ? 3001 : 3000;

			Matrix4x4 lowerMatrix = Matrix4x4.Scale(Vector3.one * (lowerRadius * edgeInflate));
			Graphics.DrawMesh(sphere, lowerMatrix, lowerMaterial, drawLayer, drawCamera);

			if (settings.UpperActive)
			{
				Matrix4x4 upperMatrix = Matrix4x4.Scale(Vector3.one * (upperRadius * edgeInflate));
				Graphics.DrawMesh(sphere, upperMatrix, upperMaterial, drawLayer, drawCamera);
			}
		}

		/// <summary>
		/// The camera to draw into. Cached rather than found per frame, and falling back to
		/// Camera.main, which is how the rest of this project resolves the game camera.
		/// </summary>
		Camera ResolveCamera()
		{
			if (cachedCamera == null) { cachedCamera = Camera.main; }
			return cachedCamera;
		}

		void EnsureResources()
		{
			// A unit sphere, scaled to each deck's radius by the draw matrix. One mesh, two draws -
			// the decks differ only in radius, so a second copy would be the same vertices again.
			if (sphere == null)
			{
				// MeshHelper picks the 16- or 32-bit index format from the vertex count, and computes
				// the bounds - which Unity then transforms by the draw matrix, so the unit sphere's
				// bounds scale to the deck's radius for culling without anything further here.
				sphere = MeshHelper.CreateMesh(IcoSphere.Generate(resolution, 1f));
				sphere.name = "Baseline Cloud Shell";
			}

			if (lowerMaterial == null)
			{
				lowerMaterial = new Material(shellShader) { hideFlags = HideFlags.HideAndDontSave };
			}
			if (upperMaterial == null)
			{
				upperMaterial = new Material(shellShader) { hideFlags = HideFlags.HideAndDontSave };
			}
		}
	}
}
