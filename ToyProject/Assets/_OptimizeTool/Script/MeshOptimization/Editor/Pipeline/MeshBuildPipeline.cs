using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.OptimizeTool.MeshOptimization
{
    public static class MeshBuildPipeline
    {
        /// <summary>
        /// Builds the combined meshes of <paramref name="sources"/> in the space of
        /// <paramref name="destinationWorldToLocal"/>. The result owns the new meshes; dispose it.
        /// </summary>
        public static MeshBuildResult Build(IReadOnlyList<MeshFilter> sources, Matrix4x4 destinationWorldToLocal, MeshBuildSettings settings)
        {
            AssetFileTransaction.RequireEditMode();
            if (sources == null) throw new ArgumentNullException(nameof(sources));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();
            var copy = new MeshFilter[sources.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = sources[i];
            MeshRendererSettings rendererSettings = ValidateSources(copy);
            return BuildValidated(copy, destinationWorldToLocal, settings, rendererSettings);
        }

        private static MeshRendererSettings ValidateSources(IReadOnlyList<MeshFilter> sources)
        {
            if (sources.Count == 0) throw new ArgumentException("No source MeshFilters were supplied.");
            var seen = new HashSet<MeshFilter>();
            MeshRendererSettings rendererSettings = null;
            foreach (MeshFilter filter in sources)
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

        private static MeshBuildResult BuildValidated(IReadOnlyList<MeshFilter> sources, Matrix4x4 destinationWorldToLocal,
            MeshBuildSettings settings, MeshRendererSettings rendererSettings)
        {
            var result = new MeshBuildResult { RendererSettings = rendererSettings };
            if (settings.BackfaceCulling || settings.OcclusionCulling)
                result.AddWarning("View-dependent bake: removed geometry may still be needed by other cameras, shadows or reflections.");
            if (settings.OcclusionCulling)
                result.AddWarning("Occlusion treats every external non-trigger Collider on the mask as opaque, including invisible or transparent proxies. Use dedicated occluder layers.");
            var temporary = new List<Mesh>();
            var readableSources = new Dictionary<Mesh, Mesh>();
            var materialGroups = new Dictionary<Material, List<CombineInstance>>();
            var materialOrder = new List<Material>();
            try
            {
                Physics.SyncTransforms();
                foreach (MeshFilter filter in sources)
                {
                    MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                    if (renderer == null) throw new ArgumentException($"'{filter.name}' needs a MeshRenderer.");
                    Mesh original = filter.sharedMesh;
                    if (original == null) throw new ArgumentException($"'{filter.name}' has no mesh.");
                    if (!readableSources.TryGetValue(original, out Mesh mesh))
                    {
                        mesh = MeshGeometry.ReadableCopy(original, settings.AllowReadableCopy, settings.AllowNormalRecalculation);
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
                    if (settings.OcclusionCulling && MeshOcclusion.IsOccluded(filter, mesh, settings.Camera,
                        settings.OcclusionSampleCount, settings.VisibilityThreshold, settings.OcclusionLayerMask))
                    {
                        result.AddRemoved(renderer);
                        continue;
                    }
                    if (settings.BackfaceCulling)
                    {
                        Mesh culled = MeshGeometry.CullBackfaces(mesh, filter.transform, settings.Camera, settings.BackfaceAngle);
                        if (culled == null) { result.AddRemoved(renderer); continue; }
                        temporary.Add(culled);
                        mesh = culled;
                    }
                    Mesh culledForSource = mesh != readableSources[original] ? mesh : null;
                    Matrix4x4 matrix = destinationWorldToLocal * filter.transform.localToWorldMatrix;
                    for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                    {
                        if (mesh.GetIndexCount(submesh) == 0) continue;
                        Mesh part = MeshGeometry.SelectTriangles(mesh,
                            new List<int[]> { mesh.GetTriangles(submesh) }, mesh.name + "_Part");
                        temporary.Add(part);
                        MeshGeometry.TransformMesh(part, matrix);
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
                    result.Add(MeshGeometry.Combine(group, true, settings.OptimizeMesh), new[] { material });
                    foreach (CombineInstance input in group) UnityEngine.Object.DestroyImmediate(input.mesh);
                    group.Clear();
                }
                if (result.Parts.Count == 0) throw new InvalidOperationException("No triangles remained after culling.");
                if (settings.CombineIntoSingleMesh && result.Parts.Count > 1)
                {
                    var combines = new List<CombineInstance>();
                    foreach (MeshBuildPart part in result.Parts) combines.Add(new CombineInstance { mesh = part.Mesh, transform = Matrix4x4.identity });
                    Mesh single = MeshGeometry.Combine(combines, false, settings.OptimizeMesh);
                    result.ReplaceWithSingle(single, materialOrder.ToArray());
                }
                return result;
            }
            catch { result.Dispose(); throw; }
            finally { foreach (Mesh mesh in temporary) if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh); }
        }
    }
}
