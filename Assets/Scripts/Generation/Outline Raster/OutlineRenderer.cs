using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class OutlineRenderer : MonoBehaviour
{
	public float width;

	public Shader lineShader;
	public Shader lineJoinsShader;
	public int circleJoinResolution;

	Mesh lineSegmentMesh;
	Mesh circleJoinMesh;

	List<LineMesh> lines;


	public void Add(LineSegment[] lineSegments, Color colour)
	{
		LineMesh line = new LineMesh(lineSegments, colour);
		line.Prepare(lineShader, lineJoinsShader, lineSegmentMesh, circleJoinMesh);
		lines.Add(line);
	}


	void Awake()
	{
		CreateLineMesh();
		CreateCircleJoinMesh();
		lines = new List<LineMesh>();
	}


	void Update()
	{
		for (int i = 0; i < lines.Count; i++)
		{
			lines[i].Draw(width);
		}
	}

	void CreateLineMesh()
	{
		lineSegmentMesh = LineMeshUtility.CreateLineSegmentMesh();
	}

	void CreateCircleJoinMesh()
	{
		circleJoinMesh = LineMeshUtility.CreateCircleJoinMesh(circleJoinResolution);
	}

	public class LineMesh
	{
		LineSegment[] lineSegments;
		Bounds bounds;
		ComputeBuffer lineSegmentsBuffer;
		ComputeBuffer lineArgsBuffer;
		ComputeBuffer joinsArgsBuffer;
		Material lineMat;
		Material joinsMat;
		Mesh lineSegmentMesh;
		Mesh circleJoinMesh;
		Color colour;

		public LineMesh(LineSegment[] lineSegments, Color colour)
		{
			this.lineSegments = lineSegments;
			this.colour = colour;
		}

		public void Prepare(Shader lineShader, Shader joinsShader, Mesh lineSegmentMesh, Mesh circleJoinMesh)
		{
			this.lineSegmentMesh = lineSegmentMesh;
			this.circleJoinMesh = circleJoinMesh;

			// Create buffers
			ComputeHelper.CreateStructuredBuffer<LineSegment>(ref lineSegmentsBuffer, lineSegments.Length);
			lineSegmentsBuffer.SetData(lineSegments);

			lineArgsBuffer = ComputeHelper.CreateArgsBuffer(lineSegmentMesh, lineSegments.Length);
			joinsArgsBuffer = ComputeHelper.CreateArgsBuffer(circleJoinMesh, lineSegments.Length);

			// Calculate bounds
			bounds = new Bounds(lineSegments[0].pointA, Vector3.zero);
			for (int i = 1; i < lineSegments.Length; i++)
			{
				bounds.Encapsulate(lineSegments[i].pointB);
			}

			// Create materials
			lineMat = new Material(lineShader);
			joinsMat = new Material(joinsShader);

			lineMat.SetBuffer("lineSegments", lineSegmentsBuffer);
			joinsMat.SetBuffer("lineSegments", lineSegmentsBuffer);
		}

		public void Draw(float width)
		{
			lineMat.SetColor("colour", colour);
			lineMat.SetFloat("width", width);
			Graphics.DrawMeshInstancedIndirect(lineSegmentMesh, 0, lineMat, bounds, lineArgsBuffer);

			joinsMat.SetColor("colour", colour);
			joinsMat.SetFloat("width", width);
			Graphics.DrawMeshInstancedIndirect(circleJoinMesh, 0, joinsMat, bounds, joinsArgsBuffer);
		}

		public void Release()
		{
			ComputeHelper.Release(lineSegmentsBuffer, joinsArgsBuffer, lineArgsBuffer);
		}
	}


	void OnDestroy()
	{
		for (int i = 0; i < lines.Count; i++)
		{
			lines[i].Release();
		}
	}
}
public struct LineSegment
{
	public Vector3 pointA;
	public Vector3 pointB;
}
