#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace Common.OptimizeTool
{
    /// <summary>Build inputs and geometry options only; no save path or scene mutation policy.</summary>
    public sealed class MeshBuildRequest
    {
        public ReadOnlyCollection<MeshFilter> Sources { get; }
        public Matrix4x4 DestinationWorldToLocal { get; }
        public Camera Camera { get; }
        public bool AllowReadableCopy { get; }
        public bool AllowNormalRecalculation { get; }
        public bool Optimize { get; }
        public bool SingleMesh { get; }
        public bool Backfaces { get; }
        public float BackfaceAngle { get; }
        public bool Occlusion { get; }
        public int SamplingCount { get; }
        public double VisibilityThreshold { get; }
        public int LayerMask { get; }

        public MeshBuildRequest(IList<MeshFilter> sources, Matrix4x4 destinationWorldToLocal,
            Camera camera = null, bool allowReadableCopy = false, bool allowNormalRecalculation = false,
            bool optimize = true, bool singleMesh = false, bool backfaces = false, float backfaceAngle = 90f,
            bool occlusion = false, int samplingCount = 100, double visibilityThreshold = 0.01, int layerMask = ~0)
        {
            if (sources == null) throw new ArgumentNullException(nameof(sources));
            Sources = new List<MeshFilter>(sources).AsReadOnly();
            DestinationWorldToLocal = destinationWorldToLocal; Camera = camera;
            AllowReadableCopy = allowReadableCopy; AllowNormalRecalculation = allowNormalRecalculation;
            Optimize = optimize; SingleMesh = singleMesh; Backfaces = backfaces; BackfaceAngle = backfaceAngle;
            Occlusion = occlusion; SamplingCount = samplingCount; VisibilityThreshold = visibilityThreshold; LayerMask = layerMask;
        }
    }

    public static class MeshBuildPipeline
    {
        public static MeshBuildResult Build(MeshBuildRequest request)
        {
            MeshRendererSettings rendererSettings = Validate(request);
            return BuildValidated(request, rendererSettings);
        }

        internal static MeshRendererSettings Validate(MeshBuildRequest request)
        {
            MeshProcessingUtility.RequireEditMode();
            if (request == null) throw new ArgumentNullException(nameof(request));
            MeshProcessingUtility.ValidateAngle(request.BackfaceAngle);
            if ((request.Backfaces || request.Occlusion) && request.Camera == null)
                throw new ArgumentException("Culling requires a target camera.");
            if (request.Occlusion && (request.SamplingCount <= 0 || double.IsNaN(request.VisibilityThreshold)
                || double.IsInfinity(request.VisibilityThreshold) || request.VisibilityThreshold < 0
                || request.VisibilityThreshold > 1 || request.LayerMask == 0))
                throw new ArgumentException("Occlusion sampling settings are invalid.");
            if (request.Sources.Count == 0) throw new ArgumentException("No source MeshFilters were supplied.");
            var seen = new HashSet<MeshFilter>();
            MeshRendererSettings rendererSettings = null;
            foreach (MeshFilter filter in request.Sources)
            {
                if (filter == null || !seen.Add(filter)) throw new ArgumentException("Sources must be present and unique.");
                MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null) throw new ArgumentException($"'{filter.name}' needs a MeshRenderer.");
                MeshRendererSettings.ValidateSupported(renderer);
                if (rendererSettings == null) rendererSettings = new MeshRendererSettings(renderer);
                else rendererSettings.ValidateCompatible(renderer);
            }
            return rendererSettings;
        }

        private static MeshBuildResult BuildValidated(MeshBuildRequest request, MeshRendererSettings rendererSettings)
        {
            var result = new MeshBuildResult { RendererSettings = rendererSettings };
            if (request.Backfaces || request.Occlusion)
                result.AddWarning("View-dependent bake: removed geometry may still be needed by other cameras, shadows or reflections.");
            if (request.Occlusion)
                result.AddWarning("Occlusion treats every external non-trigger Collider on the mask as opaque, including invisible or transparent proxies. Use dedicated occluder layers.");
            var temporary = new List<Mesh>();
            var readableSources = new Dictionary<Mesh, Mesh>();
            var materialGroups = new Dictionary<Material, List<CombineInstance>>();
            var materialOrder = new List<Material>();
            try
            {
                Physics.SyncTransforms();
                foreach (MeshFilter filter in request.Sources)
                {
                    MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                    if (renderer == null) throw new ArgumentException($"'{filter.name}' needs a MeshRenderer.");
                    Mesh original = filter.sharedMesh;
                    if (original == null) throw new ArgumentException($"'{filter.name}' has no mesh.");
                    if (!readableSources.TryGetValue(original, out Mesh mesh))
                    {
                        mesh = MeshProcessingUtility.ReadableCopy(original, request.AllowReadableCopy, request.AllowNormalRecalculation);
                        readableSources.Add(original, mesh); temporary.Add(mesh);
                    }
                    Material[] materials = renderer.sharedMaterials;
                    if (materials.Length != mesh.subMeshCount)
                        throw new ArgumentException($"'{filter.name}' must have one material per submesh.");
                    foreach (Material material in materials)
                        if (material == null) throw new ArgumentException($"'{filter.name}' has a null material.");
                    foreach (Material material in materials)
                        if (material.renderQueue > 2500 || material.HasProperty("_Cull") && material.GetInt("_Cull") == 0)
                            result.AddWarning("Transparent sorting and double-sided material appearance are not guaranteed by mesh merging or backface removal.");
                    if (renderer.probeAnchor == null && renderer.lightProbeUsage != UnityEngine.Rendering.LightProbeUsage.Off)
                        result.AddWarning("Merging changes renderer bounds and implicit light-probe sampling positions. Use an explicit shared probe anchor or validate lighting after baking.");
                    result.AddSource(renderer);
                    if (request.Occlusion && MeshProcessingUtility.IsOccluded(filter, mesh, request.Camera,
                        request.SamplingCount, request.VisibilityThreshold, request.LayerMask))
                    {
                        result.CulledObjectCount++;
                        continue;
                    }
                    if (request.Backfaces)
                    {
                        Mesh culled = MeshProcessingUtility.CullBackfaces(mesh, filter.transform, request.Camera,
                            request.BackfaceAngle, false, Vector3.zero);
                        if (culled == null) { result.CulledObjectCount++; continue; }
                        temporary.Add(culled);
                        mesh = culled;
                    }
                    Mesh culledForSource = mesh != readableSources[original] ? mesh : null;
                    Matrix4x4 matrix = request.DestinationWorldToLocal * filter.transform.localToWorldMatrix;
                    for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                    {
                        if (mesh.GetIndexCount(submesh) == 0) continue;
                        Mesh part = MeshProcessingUtility.SelectTriangles(mesh,
                            new List<int[]> { mesh.GetTriangles(submesh) }, mesh.name + "_Part");
                        temporary.Add(part);
                        MeshProcessingUtility.TransformMesh(part, matrix);
                        Material material = materials[submesh];
                        if (!materialGroups.TryGetValue(material, out List<CombineInstance> group))
                        {
                            group = new List<CombineInstance>();
                            materialGroups.Add(material, group);
                            materialOrder.Add(material);
                        }
                        group.Add(new CombineInstance { mesh = part, transform = Matrix4x4.identity });
                    }
                    if (culledForSource != null) UnityEngine.Object.DestroyImmediate(culledForSource);
                }
                foreach (Material material in materialOrder)
                {
                    List<CombineInstance> group = materialGroups[material];
                    result.Add(MeshProcessingUtility.Combine(group, true, request.Optimize), new[] { material });
                    foreach (CombineInstance input in group) UnityEngine.Object.DestroyImmediate(input.mesh);
                    group.Clear();
                }
                if (result.Meshes.Count == 0) throw new InvalidOperationException("No triangles remained after culling.");
                if (request.SingleMesh && result.Meshes.Count > 1)
                {
                    var combines = new List<CombineInstance>();
                    foreach (Mesh mesh in result.Meshes) combines.Add(new CombineInstance { mesh = mesh, transform = Matrix4x4.identity });
                    Mesh single = MeshProcessingUtility.Combine(combines, false, request.Optimize);
                    result.ReplaceWithSingle(single, materialOrder.ToArray());
                }
                return result;
            }
            catch { result.Dispose(); throw; }
            finally { foreach (Mesh mesh in temporary) if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh); }
        }
    }
}
#endif
