using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Common.OptimizeTool.Samples
{
    /// <summary>Flat-faced cube with real, shared-edge triangle grids; density changes vertex workload without changing its surface.</summary>
    public static class OptimizeToolBenchmarkGeometry
    {
        public static Mesh CreateCube(int subdivisions)
        {
            if (subdivisions < 1 || subdivisions > 128) throw new ArgumentOutOfRangeException(nameof(subdivisions));
            int stride = subdivisions + 1, faceVertices = stride * stride;
            var vertices = new Vector3[6 * faceVertices];
            var normals = new Vector3[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var indices = new int[36 * subdivisions * subdivisions];
            var directions = new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left, Vector3.up, Vector3.down };
            int t = 0;
            for (int face = 0; face < directions.Length; face++)
            {
                Vector3 normal = directions[face];
                Vector3 u = face < 4 ? Vector3.Cross(Vector3.up, normal) : Vector3.right;
                Vector3 v = Vector3.Cross(normal, u);
                for (int y = 0; y <= subdivisions; y++)
                for (int x = 0; x <= subdivisions; x++)
                {
                    int i = face * faceVertices + y * stride + x;
                    var texcoord = new Vector2((float)x / subdivisions, (float)y / subdivisions);
                    vertices[i] = normal * 0.5f + u * (texcoord.x - 0.5f) + v * (texcoord.y - 0.5f);
                    normals[i] = normal; uv[i] = texcoord;
                    if (x == subdivisions || y == subdivisions) continue;
                    indices[t++] = i; indices[t++] = i + 1; indices[t++] = i + stride;
                    indices[t++] = i + 1; indices[t++] = i + stride + 1; indices[t++] = i + stride;
                }
            }
            var mesh = new Mesh { name = "BenchmarkCube-" + subdivisions, indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.vertices = vertices; mesh.normals = normals; mesh.uv = uv; mesh.triangles = indices;
            mesh.RecalculateTangents(); mesh.RecalculateBounds();
            return mesh;
        }
    }
}
