using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.MeshOptimization
{
    /// <summary>
    /// The editor operations of the mesh components. They are extension methods, so editor code keeps calling
    /// <c>combiner.BuildMeshes()</c> while the components themselves hold only settings.
    /// </summary>
    public static class MeshToolCommands
    {
        /// <summary>Creates in-memory results only. Dispose the result after saving or inspecting it.</summary>
        public static MeshBuildResult BuildMeshes(this MeshCombiner combiner)
        {
            combiner.LastBuildWarnings = Array.Empty<string>();
            var result = MeshBuildPipeline.Build(CaptureBuildRequest(combiner, out bool skippedDisabledChild));
            if (skippedDisabledChild)
                result.AddWarning("Child MeshFilters with a disabled Renderer were left out. Assign them directly to bake them.");
            combiner.LastBuildWarnings = result.Warnings;
            return result;
        }

        /// <summary>Saves the combined meshes as assets and applies the scene output.</summary>
        public static Mesh[] CombineAndSaveMeshes(this MeshCombiner combiner)
        {
            var save = new MeshSaveOptions(combiner.SavePath, combiner.MeshName, combiner.OverwriteExistingAssets);
            using (MeshBuildResult result = combiner.BuildMeshes())
            {
                List<string> paths = save.CreatePaths(result.Meshes.Count, combiner.CombineAllIntoSingleMesh);
                return MeshAssetTransaction.Save(result.Meshes, paths, save.Overwrite, "Combine meshes",
                    meshes => { if (combiner.CreateSceneOutput) CreateSceneOutput(combiner, meshes, result); });
            }
        }

        /// <summary>Stages every result before changing any asset or MeshFilter.</summary>
        public static Mesh[] BakeBackFaceCullingMesh(this BackfaceCullMeshBaker baker)
        {
            ValidateSettings(baker);
            MeshFilter[] filters = baker.CollectChildMeshes ? baker.GetComponentsInChildren<MeshFilter>() : baker.TargetMeshFilters;
            if (filters.Length == 0) throw new ArgumentException("No MeshFilters were supplied.");
            var staged = new List<Mesh>();
            var paths = new List<string>();
            try
            {
                foreach (MeshFilter filter in filters)
                {
                    if (filter == null) throw new ArgumentException("A target MeshFilter is missing.");
                    Mesh readable = MeshProcessingUtility.ReadableCopy(filter.sharedMesh, baker.AllowReadableCopy, baker.AllowNormalRecalculation);
                    try
                    {
                        Mesh culled = MeshProcessingUtility.CullBackfaces(readable, filter.transform, baker.TargetCamera,
                            baker.BackfaceCullingAngle, baker.UseCustomVector, baker.CustomCameraVector);
                        if (culled == null) throw new InvalidOperationException($"All triangles were removed from '{filter.name}'.");
                        staged.Add(culled);
                        paths.Add($"{baker.SavePath}/{filter.name}_BackfaceCulled.asset");
                    }
                    finally { Object.DestroyImmediate(readable); }
                }
                return MeshAssetTransaction.Save(staged, paths, baker.OverwriteExistingAssets, "Bake backfaces", meshes =>
                {
                    for (int i = 0; i < filters.Length; i++)
                    {
                        Undo.RegisterCompleteObjectUndo(filters[i], "Assign baked mesh");
                        filters[i].sharedMesh = meshes[i];
                        PrefabUtility.RecordPrefabInstancePropertyModifications(filters[i]);
                    }
                });
            }
            finally { foreach (Mesh mesh in staged) if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        private static MeshBuildRequest CaptureBuildRequest(MeshCombiner combiner, out bool skippedDisabledChild)
        {
            skippedDisabledChild = false;
            MeshProcessingUtility.RequireEditMode();
            MeshFilter[] direct = combiner.TargetMeshFilters;
            if (combiner.CollectChildMeshes == (direct != null && direct.Length > 0))
                throw new ArgumentException("Choose direct MeshFilters or child collection, not both.");
            MeshFilter[] filters = combiner.CollectChildMeshes
                ? combiner.GetComponentsInChildren<MeshFilter>(combiner.IncludeInactive) : direct;
            GameObject generatedRoot = combiner.GeneratedRoot;
            var inputs = new List<MeshFilter>();
            foreach (MeshFilter filter in filters)
            {
                if (filter != null && generatedRoot != null && filter.transform.IsChildOf(generatedRoot.transform)) continue;
                if (combiner.CollectChildMeshes && filter != null && IsDisabledByUser(combiner, filter.GetComponent<MeshRenderer>()))
                {
                    skippedDisabledChild = true;
                    continue;
                }
                inputs.Add(filter);
            }
            return new MeshBuildRequest(inputs, combiner.transform.worldToLocalMatrix, combiner.TargetCamera,
                combiner.AllowReadableCopy, combiner.AllowNormalRecalculation, combiner.OptimizeMesh, combiner.CombineAllIntoSingleMesh,
                combiner.EnableBackfaceCulling, combiner.BackfaceCullingAngle, combiner.EnableOcclusionCulling,
                combiner.VertexSamplingCount, combiner.VisibilityThreshold, combiner.CullingLayerMask);
        }

        // A source this component disabled in an earlier bake is still an input.
        private static bool IsDisabledByUser(MeshCombiner combiner, MeshRenderer renderer)
        {
            if (renderer == null || renderer.enabled) return false;
            foreach (MeshSourceRendererState state in combiner.SourceRendererStates)
                if (state.Renderer == renderer) return !state.Enabled;
            return true;
        }

        private static void CreateSceneOutput(MeshCombiner combiner, Mesh[] meshes, MeshBuildResult result)
        {
            Undo.RegisterCompleteObjectUndo(combiner, "Assign combined result");
            // Undo can serialize the owner while Destroy/RegisterCreatedObjectUndo runs.
            // Keep its old ledger intact until every scene operation has completed.
            var nextStates = new List<MeshSourceRendererState>(combiner.SourceRendererStates);
            combiner.GeneratedRoot = MeshSceneOutput.Replace(combiner.transform, combiner.MeshName, combiner.GeneratedRoot, meshes, result,
                combiner.DisableSourceRenderers, nextStates);
            combiner.SourceRendererStates = nextStates;
            PrefabUtility.RecordPrefabInstancePropertyModifications(combiner);
        }

        private static void ValidateSettings(BackfaceCullMeshBaker baker)
        {
            MeshProcessingUtility.RequireEditMode();
            AssetFileTransaction.ValidateFolder(baker.SavePath);
            if (!baker.UseCustomVector && baker.TargetCamera == null) throw new ArgumentException("Assign a target camera.");
            if (baker.UseCustomVector && (!MeshProcessingUtility.IsFinite(baker.CustomCameraVector) || baker.CustomCameraVector.sqrMagnitude < 0.000001f))
                throw new ArgumentException("The custom view direction must be finite and non-zero.");
            MeshFilter[] direct = baker.TargetMeshFilters;
            if (baker.CollectChildMeshes == (direct != null && direct.Length > 0))
                throw new ArgumentException("Choose direct MeshFilters or child collection, not both.");
            MeshProcessingUtility.ValidateAngle(baker.BackfaceCullingAngle);
        }
    }
}
