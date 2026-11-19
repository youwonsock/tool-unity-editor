using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.MeshOptimization.Editor
{
    /// <summary>One output mesh and the materials of its submeshes, in submesh order.</summary>
    public sealed class MeshBuildPart
    {
        public Mesh Mesh { get; }
        public IReadOnlyList<Material> Materials { get; }

        internal MeshBuildPart(Mesh mesh, Material[] materials)
        {
            Mesh = mesh;
            Materials = Array.AsReadOnly((Material[])materials.Clone());
        }
    }

    /// <summary>Owns transient output meshes only. Source renderers/materials are borrowed, never destroyed.</summary>
    public sealed class MeshBuildResult : IDisposable
    {
        private readonly List<MeshBuildPart> _parts = new List<MeshBuildPart>();
        private readonly List<MeshRenderer> _sources = new List<MeshRenderer>();
        private readonly List<MeshRenderer> _removed = new List<MeshRenderer>();
        private readonly List<string> _warnings = new List<string>();
        private bool _disposed;

        public ReadOnlyCollection<MeshBuildPart> Parts { get; }
        /// <summary>Every input renderer, including the removed ones.</summary>
        public ReadOnlyCollection<MeshRenderer> SourceRenderers { get; }
        /// <summary>Inputs that occlusion or backface culling removed completely.</summary>
        public ReadOnlyCollection<MeshRenderer> RemovedSources { get; }
        public ReadOnlyCollection<string> Warnings { get; }
        public MeshRendererSettings RendererSettings { get; internal set; }

        internal MeshBuildResult()
        {
            Parts = _parts.AsReadOnly(); SourceRenderers = _sources.AsReadOnly();
            RemovedSources = _removed.AsReadOnly(); Warnings = _warnings.AsReadOnly();
        }

        /// <summary>The output meshes in part order.</summary>
        public Mesh[] GetMeshes()
        {
            var meshes = new Mesh[_parts.Count];
            for (int i = 0; i < meshes.Length; i++) meshes[i] = _parts[i].Mesh;
            return meshes;
        }

        internal void AddSource(MeshRenderer source) => _sources.Add(source);
        internal void AddRemoved(MeshRenderer source) => _removed.Add(source);
        internal void AddWarning(string warning) { if (!_warnings.Contains(warning)) _warnings.Add(warning); }
        internal void Add(Mesh mesh, Material[] materials)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MeshBuildResult));
            if (mesh == null || UnityEditor.EditorUtility.IsPersistent(mesh))
                throw new ArgumentException("Build results may own only transient meshes.");
            _parts.Add(new MeshBuildPart(mesh, materials));
        }
        internal void ReplaceWithSingle(Mesh mesh, Material[] materials)
        {
            DestroyMeshes();
            Add(mesh, materials);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DestroyMeshes();
            _sources.Clear(); _removed.Clear();
        }

        private void DestroyMeshes()
        {
            foreach (MeshBuildPart part in _parts) if (part.Mesh != null) Object.DestroyImmediate(part.Mesh);
            _parts.Clear();
        }
    }
}
