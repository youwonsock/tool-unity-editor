using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.MeshOptimization
{
    public static class MeshBuildPipeline
    {
        private const string TransparentWarning = "Transparent sorting and double-sided material appearance are not guaranteed by mesh merging or backface removal.";
        private const string ProbeWarning = "Merging changes renderer bounds and implicit light-probe sampling positions. Use an explicit shared probe anchor or validate lighting after baking.";
        private const string ViewWarning = "View-dependent bake: removed geometry may still be needed by other cameras, shadows or reflections.";
        private const string PackedLightmapWarning = "Each source got its own equal tile of the lightmap UVs (UV2). Lightmap resolution per source follows the tile, not the source size.";
        private const string MissingLightmapUvWarning = "Some lightmap static sources have no lightmap UVs (UV2); their UV0 was packed instead and may overlap itself. Enable Generate Lightmap UVs on their importer.";
        private const string OverlappingLightmapWarning = "The combined sources share lightmap UV space, so a lightmap baked for this output overlaps. Enable Pack Lightmap UVs or bake lighting before combining.";
        private const string OccluderWarning = "Occlusion treats every external non-trigger Collider on the mask as opaque, including invisible or transparent proxies. Use dedicated occluder layers.";

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
            var inputs = new MeshFilter[sources.Count];
            for (int i = 0; i < inputs.Length; i++) inputs[i] = sources[i];
            var sourceWarnings = new List<string>();
            MeshRendererSettings rendererSettings = ValidateSources(inputs, sourceWarnings);

            var result = new MeshBuildResult { RendererSettings = rendererSettings };
            if (settings.BackfaceCulling || settings.OcclusionCulling) result.AddWarning(ViewWarning);
            if (settings.OcclusionCulling) result.AddWarning(OccluderWarning);
            foreach (string warning in sourceWarnings) result.AddWarning(warning);
            var work = new BuildWork { PackLightmaps = settings.PackLightmapUVs && rendererSettings.UsesBakedLightmaps };
            if (rendererSettings.UsesBakedLightmaps)
            {
                if (!work.PackLightmaps) result.AddWarning(OverlappingLightmapWarning);
                else
                {
                    result.AddWarning(PackedLightmapWarning);
                    foreach (MeshFilter filter in inputs)
                        if (!filter.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord1)) { result.AddWarning(MissingLightmapUvWarning); break; }
                }
            }
            try
            {
                Physics.SyncTransforms();
                foreach (MeshFilter filter in inputs) ProcessSource(filter, destinationWorldToLocal, settings, work, result);
                if (work.PackLightmaps) MeshGeometry.PackLightmapUVs(work.LightmapParts);
                CombineGroups(work, settings, result);
                if (result.Parts.Count == 0) throw new InvalidOperationException("No triangles remained after culling.");
                if (settings.CombineIntoSingleMesh && result.Parts.Count > 1) MergeToSingle(work, settings, result);
                return result;
            }
            catch { result.Dispose(); throw; }
            finally { work.DestroyTemporaries(); }
        }

        /// <summary>
        /// Checks every input before any mesh is copied and returns the renderer settings they share. Warnings are
        /// collected in input order; <see cref="MeshBuildResult"/> keeps one of each.
        /// </summary>
        private static MeshRendererSettings ValidateSources(IReadOnlyList<MeshFilter> sources, List<string> warnings)
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
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) throw new ArgumentException($"'{filter.name}' has no mesh.");
                Material[] materials = renderer.sharedMaterials;
                if (materials.Length != mesh.subMeshCount)
                    throw new ArgumentException($"'{filter.name}' must have one material per submesh.");
                foreach (Material material in materials)
                    if (material == null) throw new ArgumentException($"'{filter.name}' has a null material.");
                foreach (Material material in materials)
                    if (material.renderQueue > 2500 || material.HasProperty("_Cull") && material.GetInt("_Cull") == 0)
                        warnings.Add(TransparentWarning);
                if (renderer.probeAnchor == null && renderer.lightProbeUsage != LightProbeUsage.Off)
                    warnings.Add(ProbeWarning);
            }
            return rendererSettings;
        }

        /// <summary>Culls one source and adds its submeshes, moved into the destination space, to the material groups.</summary>
        private static void ProcessSource(MeshFilter filter, Matrix4x4 destinationWorldToLocal, MeshBuildSettings settings,
            BuildWork work, MeshBuildResult result)
        {
            MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
            Material[] materials = renderer.sharedMaterials;
            Mesh mesh = work.ReadableCopy(filter.sharedMesh, settings);
            result.AddSource(renderer);
            if (settings.OcclusionCulling && MeshOcclusion.IsOccluded(filter, mesh, settings.Camera,
                settings.OcclusionSampleCount, settings.VisibilityThreshold, settings.OcclusionLayerMask))
            {
                result.AddRemoved(renderer);
                return;
            }
            Mesh culled = null;
            if (settings.BackfaceCulling)
            {
                culled = MeshGeometry.CullBackfaces(mesh, filter.transform, settings.Camera, settings.BackfaceAngle);
                if (culled == null) { result.AddRemoved(renderer); return; }
                work.Temporaries.Add(culled);
                mesh = culled;
            }
            Matrix4x4 matrix = destinationWorldToLocal * filter.transform.localToWorldMatrix;
            var sourceParts = work.PackLightmaps ? new List<Mesh>() : null;
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                if (mesh.GetIndexCount(submesh) == 0) continue;
                Mesh part = MeshGeometry.SelectTriangles(mesh, new List<int[]> { mesh.GetTriangles(submesh) }, mesh.name + "_Part");
                work.Temporaries.Add(part);
                MeshGeometry.TransformMesh(part, matrix);
                work.AddToGroup(materials[submesh], part);
                sourceParts?.Add(part);
            }
            if (sourceParts != null && sourceParts.Count > 0) work.LightmapParts.Add(sourceParts);
            // The culled copy belongs to this source only; free it before the next source.
            if (culled != null) Object.DestroyImmediate(culled);
        }

        /// <summary>Combines each material group into one output mesh, freeing the group's parts right away.</summary>
        private static void CombineGroups(BuildWork work, MeshBuildSettings settings, MeshBuildResult result)
        {
            foreach (Material material in work.MaterialOrder)
            {
                List<CombineInstance> group = work.Groups[material];
                result.Add(MeshGeometry.Combine(group, true, settings.OptimizeMesh), new[] { material });
                foreach (CombineInstance input in group) Object.DestroyImmediate(input.mesh);
                group.Clear();
            }
        }

        /// <summary>Replaces the per-material outputs with one mesh that has one submesh per material.</summary>
        private static void MergeToSingle(BuildWork work, MeshBuildSettings settings, MeshBuildResult result)
        {
            var combines = new List<CombineInstance>();
            foreach (MeshBuildPart part in result.Parts) combines.Add(new CombineInstance { mesh = part.Mesh, transform = Matrix4x4.identity });
            Mesh single = MeshGeometry.Combine(combines, false, settings.OptimizeMesh);
            result.ReplaceWithSingle(single, work.MaterialOrder.ToArray());
        }

        /// <summary>
        /// Intermediate state of one build. Every temporary mesh is listed so an exception frees it too; the steps
        /// also free parts and culled copies as soon as they are used, to keep peak memory low for large inputs.
        /// </summary>
        private sealed class BuildWork
        {
            private readonly Dictionary<Mesh, Mesh> _readableCopies = new Dictionary<Mesh, Mesh>();
            internal readonly List<Mesh> Temporaries = new List<Mesh>();
            internal readonly Dictionary<Material, List<CombineInstance>> Groups = new Dictionary<Material, List<CombineInstance>>();
            internal readonly List<Material> MaterialOrder = new List<Material>();
            /// <summary>Whether lightmap UVs are packed, and the parts of every kept source in input order.</summary>
            internal bool PackLightmaps;
            internal readonly List<List<Mesh>> LightmapParts = new List<List<Mesh>>();

            /// <summary>One readable copy per source mesh, shared by every filter that uses it.</summary>
            internal Mesh ReadableCopy(Mesh original, MeshBuildSettings settings)
            {
                if (!_readableCopies.TryGetValue(original, out Mesh copy))
                {
                    copy = MeshGeometry.ReadableCopy(original, settings.AllowReadableCopy, settings.AllowNormalRecalculation);
                    _readableCopies.Add(original, copy);
                    Temporaries.Add(copy);
                }
                return copy;
            }

            internal void AddToGroup(Material material, Mesh part)
            {
                if (!Groups.TryGetValue(material, out List<CombineInstance> group))
                {
                    group = new List<CombineInstance>();
                    Groups.Add(material, group);
                    MaterialOrder.Add(material);
                }
                group.Add(new CombineInstance { mesh = part, transform = Matrix4x4.identity });
            }

            internal void DestroyTemporaries()
            {
                foreach (Mesh mesh in Temporaries) if (mesh != null) Object.DestroyImmediate(mesh);
            }
        }
    }
}
