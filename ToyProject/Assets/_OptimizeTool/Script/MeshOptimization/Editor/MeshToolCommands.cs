using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.OptimizeTool.MeshOptimization
{
    /// <summary>
    /// The editor operations of <see cref="MeshCombiner"/>. They are extension methods, so editor code keeps calling
    /// <c>combiner.BuildMeshes()</c> while the component itself holds only settings.
    /// </summary>
    public static class MeshToolCommands
    {
        /// <summary>Creates in-memory results only. Dispose the result after saving or inspecting it.</summary>
        public static MeshBuildResult BuildMeshes(this MeshCombiner combiner)
        {
            combiner.LastBuildWarnings = Array.Empty<string>();
            MeshFilter[] sources = CollectSources(combiner, out bool skippedDisabledChild);
            var result = MeshBuildPipeline.Build(sources, combiner.transform.worldToLocalMatrix, combiner.CreateSettings());
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
                Mesh[] meshes = result.GetMeshes();
                List<string> paths = save.CreatePaths(meshes.Length, combiner.CreateSettings().CombineIntoSingleMesh);
                return MeshAssetTransaction.Save(meshes, paths, save.Overwrite, "Combine meshes",
                    saved => { if (combiner.CreateSceneOutput) CreateSceneOutput(combiner, saved, result); });
            }
        }

        private static MeshFilter[] CollectSources(MeshCombiner combiner, out bool skippedDisabledChild)
        {
            skippedDisabledChild = false;
            AssetFileTransaction.RequireEditMode();
            MeshFilter[] direct = combiner.TargetMeshFilters;
            bool hasDirect = direct != null && direct.Length > 0;
            if (combiner.CollectChildMeshes && hasDirect)
                throw new ArgumentException("Choose direct MeshFilters or child collection, not both.");
            if (!combiner.CollectChildMeshes && !hasDirect)
                throw new ArgumentException("Assign target MeshFilters or enable child collection.");
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
            return inputs.ToArray();
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
    }
}
