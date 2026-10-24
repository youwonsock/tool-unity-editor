#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool
{
    public sealed class MeshBuildPart
    {
        private readonly Material[] _materials;
        public Mesh Mesh { get; }
        public Material[] Materials => (Material[])_materials.Clone();
        internal MeshBuildPart(Mesh mesh, Material[] materials) { Mesh = mesh; _materials = (Material[])materials.Clone(); }
    }

    /// <summary>Owns transient output meshes only. Source renderers/materials are borrowed, never destroyed.</summary>
    public sealed class MeshBuildResult : IDisposable
    {
        private readonly List<MeshBuildPart> _parts = new List<MeshBuildPart>();
        private readonly List<Mesh> _meshes = new List<Mesh>();
        private readonly List<MeshRenderer> _sources = new List<MeshRenderer>();
        private readonly List<string> _warnings = new List<string>();
        private bool _disposed;
        public ReadOnlyCollection<MeshBuildPart> Parts { get; }
        public ReadOnlyCollection<Mesh> Meshes { get; }
        public ReadOnlyCollection<MeshRenderer> SourceRenderers { get; }
        public ReadOnlyCollection<string> Warnings { get; }
        public MeshRendererSettings RendererSettings { get; internal set; }
        public int CulledObjectCount { get; internal set; }

        internal MeshBuildResult()
        {
            Parts = _parts.AsReadOnly(); Meshes = _meshes.AsReadOnly(); SourceRenderers = _sources.AsReadOnly();
            Warnings = _warnings.AsReadOnly();
        }
        internal void AddSource(MeshRenderer source) => _sources.Add(source);
        internal void AddWarning(string warning) { if (!_warnings.Contains(warning)) _warnings.Add(warning); }
        internal void Add(Mesh mesh, Material[] materials)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MeshBuildResult));
            if (mesh == null || UnityEditor.EditorUtility.IsPersistent(mesh))
                throw new ArgumentException("Build results may own only transient meshes.");
            _parts.Add(new MeshBuildPart(mesh, materials)); _meshes.Add(mesh);
        }
        internal void ReplaceWithSingle(Mesh mesh, Material[] materials)
        {
            foreach (Mesh old in _meshes) if (old != null) Object.DestroyImmediate(old);
            _parts.Clear(); _meshes.Clear(); Add(mesh, materials);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Mesh mesh in _meshes) if (mesh != null) Object.DestroyImmediate(mesh);
            _parts.Clear(); _meshes.Clear(); _sources.Clear();
        }
    }
}
#endif
