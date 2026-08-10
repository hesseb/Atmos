using Seb.Meshing;
using UnityEngine;

/// <summary>
/// Anything that draws geometry sitting on the globe and can hold one copy per planet scale.
///
/// Terrain, country outlines and the ocean all need the same treatment and are loaded by two
/// different components, so the controller talks to them through this rather than knowing which
/// is which.
/// </summary>
public interface IPlanetScaleSelectable
{
	/// <summary>Activates the copy built for `scale`; false if none was built.</summary>
	bool SelectScale(float scale);
}

/// <summary>
/// Rewrites globe geometry so a uniform planet scale leaves surface relief at its authored
/// world-unit height.
/// </summary>
public static class PlanetRelief
{
	/// <summary>
	/// Moves geometry onto a planet of radius `baseRadius * planetScale`, keeping height above
	/// the surface unchanged: |v| = R0 + h becomes R0*k + h.
	///
	/// **The scale is baked into the vertices, and the World transform is left at 1.** That is
	/// deliberate and was arrived at the hard way. Scaling the root instead is equivalent for
	/// rendering but not for culling: these meshes are statically batched, and
	/// StaticBatchingUtility bakes each renderer's bounds in world space at combine time. Scaling
	/// the root afterwards leaves every chunk with bounds from its unscaled position, so Unity
	/// frustum-culls chunks that are actually on screen - whole panels of the planet blinking out
	/// as the camera pulls back, and worse the larger the scale.
	///
	/// Keeping height above the surface fixed is the other half. A uniform scale would take relief
	/// with it, and a real planet sixteen times larger does not have sixteen times taller
	/// mountains - against an atmosphere scale height that does not scale either, 48-unit peaks
	/// would stand absurdly clear of the haze with the camera buried inside them.
	///
	/// It matters beyond terrain: country outlines sit at a small offset above the surface, and
	/// scaling that offset leaves borders floating in the sky. The ocean is a sphere at the base
	/// radius, so h is zero and it simply moves to the new radius - which is why one correction
	/// serves everything on the globe with no special cases.
	/// </summary>
	public static void Correct(SimpleMeshData mesh, float baseRadius, float planetScale)
	{
		if (mesh == null || Mathf.Approximately(planetScale, 1f)) { return; }

		Vector3[] vertices = mesh.vertices;
		for (int i = 0; i < vertices.Length; i++)
		{
			float radius = vertices[i].magnitude;
			if (radius <= 1e-5f) { continue; }

			float relief = radius - baseRadius;
			vertices[i] *= (baseRadius * planetScale + relief) / radius;
		}
	}
}
