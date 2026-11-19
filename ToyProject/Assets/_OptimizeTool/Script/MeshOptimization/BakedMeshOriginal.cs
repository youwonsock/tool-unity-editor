using System;
using UnityEngine;

namespace Common.OptimizeTool.MeshOptimization
{
    /// <summary>The mesh a backface bake replaced on one MeshFilter, and the baked mesh it assigned there.</summary>
    [Serializable]
    internal sealed class BakedMeshOriginal
    {
        public MeshFilter Filter;
        public Mesh Original;
        public Mesh Baked;
    }
}
