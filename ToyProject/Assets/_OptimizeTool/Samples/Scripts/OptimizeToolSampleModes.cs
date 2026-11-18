using System;
using System.Collections.ObjectModel;

namespace Common.OptimizeTool.Samples
{
    public static class OptimizeToolSampleModes
    {
        // Keep these indices stable: existing prefab arrays and recorded benchmark rows use them.
        public const int Original = 0, Combined = 1, Backface = 2, Occlusion = 3, Physics = 4;
        public const int MeshCount = 4;
        public sealed class Definition
        {
            public string Name { get; }
            public string Button { get; }
            public string Group { get; }
            internal Definition(string name, string button, string group) { Name = name; Button = button; Group = group; }
        }
        public static readonly ReadOnlyCollection<Definition> All = Array.AsReadOnly(new[]
        {
            new Definition("Original Meshes", "ORIGINAL", "OriginalMeshes"),
            new Definition("Combined Mesh", "COMBINED", "OptimizedMeshes"),
            new Definition("Backface Bake", "BACKFACE", "BackfaceCullMeshes"),
            new Definition("Occlusion Bake", "OCCLUSION", "OcclusionCullMeshes"),
            new Definition("Physics Recorder", "PHYSICS", "PhysicsRecorderDemo")
        });
        public static int Count => All.Count;
    }
}
