using System;
using UnityEngine;

namespace Common.OptimizeTool.MeshOptimization
{
    /// <summary>The enabled state a source Renderer had before a combine disabled it, kept so a rebuild can restore it.</summary>
    [Serializable]
    internal sealed class MeshSourceRendererState
    {
        public Renderer Renderer;
        public bool Enabled;
    }
}
