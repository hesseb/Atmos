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
	/// Pre-divides height above the base radius, so that scaling the result by `planetScale`
	/// restores it: |v| = R0 + h becomes R0 + h/k, which scales to R0*k + h.
	///
	/// Without this a uniform transform scales relief along with the radius, which is wrong twice
	/// over. A real planet sixteen times larger does not have sixteen times taller mountains, and
	/// against an atmosphere scale height that does *not* scale, 48-unit peaks stand absurdly
	/// clear of the haze while a camera at altitude 10 ends up inside them.
	///
	/// It matters for more than terrain. Country outlines are drawn at a small offset above the
	/// surface; scaling that offset by sixteen leaves borders floating in the sky. The ocean is a
	/// sphere at the base radius, so h is zero and this is a no-op for it - which is exactly why
	/// the same correction can be applied to everything on the globe without special-casing.
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
			vertices[i] *= (baseRadius + relief / planetScale) / radius;
		}
	}
}
