using UnityEngine;

/// <summary>
/// Unit meshes for screen-space-width instanced line rendering.
///
/// Extracted from OutlineRenderer so the runtime country highlight can share them rather
/// than duplicating the vertex layouts, which the line shaders depend on:
///   - segment quad: x spans 0..1 along the line, y spans -0.5..0.5 across it
///   - circle join: vertex 0 at the centre, the rest on the unit circle
/// </summary>
public static class LineMeshUtility
{
	public static Mesh CreateLineSegmentMesh()
	{
		Mesh mesh = new Mesh { name = "Line Segment Quad" };

		Vector3[] vertices = {
			new Vector3(0, -0.5f), // bottom left
			new Vector3(1, -0.5f), // bottom right
			new Vector3(1, 0.5f),  // top right
			new Vector3(0, 0.5f)   // top left
		};
		int[] triangles = { 0, 2, 1, 0, 3, 2 };

		mesh.SetVertices(vertices);
		mesh.SetTriangles(triangles, 0, true);
		return mesh;
	}

	public static Mesh CreateCircleJoinMesh(int resolution)
	{
		int numIncrements = Mathf.Max(3, resolution);

		float angleIncrement = (2 * Mathf.PI) / (numIncrements - 1f);
		var verts = new Vector3[numIncrements + 1];
		var tris = new int[(numIncrements - 1) * 3];
		verts[0] = Vector3.zero;

		for (int i = 0; i < numIncrements; i++)
		{
			float currAngle = angleIncrement * i;
			verts[i + 1] = new Vector3(Mathf.Sin(currAngle), Mathf.Cos(currAngle), 0);

			if (i < numIncrements - 1)
			{
				tris[i * 3] = 0;
				tris[i * 3 + 1] = i + 1;
				tris[i * 3 + 2] = i + 2;
			}
		}

		Mesh mesh = new Mesh { name = "Line Join Circle" };
		mesh.SetVertices(verts);
		mesh.SetTriangles(tris, 0, true);
		return mesh;
	}
}
