using System;
using UnityEngine;

namespace Common.OptimizeTool.Samples
{
    internal readonly struct MeshSampleMetrics
    {
        public int Renderers { get; }
        public int Vertices { get; }
        public long Triangles { get; }
        private MeshSampleMetrics(int renderers, int vertices, long triangles)
        { Renderers = renderers; Vertices = vertices; Triangles = triangles; }

        public static MeshSampleMetrics Capture(GameObject root)
        {
            int vertices = 0; long triangles = 0;
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true)) Add(filter.sharedMesh);
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) Add(renderer.sharedMesh);
            return new MeshSampleMetrics(root.GetComponentsInChildren<Renderer>(true).Length, vertices, triangles);

            void Add(Mesh mesh)
            {
                if (mesh == null) throw new ArgumentException("A sample renderer has no mesh.");
                vertices = checked(vertices + mesh.vertexCount);
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++) triangles += (long)mesh.GetIndexCount(submesh) / 3;
            }
        }
    }
}
