using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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
        public static MeshBuildResult BuildMeshes(this MeshCombiner combiner) => Build(combiner, combiner.CreateSettings());

        private static MeshBuildResult Build(MeshCombiner combiner, MeshBuildSettings settings)
        {
            combiner.LastBuildWarnings = Array.Empty<string>();
            MeshFilter[] sources = CollectSources(combiner, out bool skippedDisabledChild);
            var result = MeshBuildPipeline.Build(sources, combiner.transform.worldToLocalMatrix, settings);
            if (skippedDisabledChild)
                result.AddWarning("Child MeshFilters with a disabled Renderer were left out. Assign them directly to bake them.");
            combiner.LastBuildWarnings = result.Warnings;
            return result;
        }

        /// <summary>Saves the combined meshes as assets and applies the scene output.</summary>
        public static Mesh[] CombineAndSaveMeshes(this MeshCombiner combiner)
        {
            string folder = AssetFileTransaction.ValidateFolder(combiner.SavePath);
            AssetFileTransaction.ValidateFileName(combiner.MeshName);
            if (combiner.CreateSceneOutput) MeshSceneOutput.RequireReplaceable(combiner.GeneratedRoot);
            MeshBuildSettings settings = combiner.CreateSettings();
            using (MeshBuildResult result = Build(combiner, settings))
            {
                Mesh[] meshes = result.GetMeshes();
                List<string> paths = OutputPaths(folder, combiner.MeshName, meshes.Length, settings.CombineAllIntoSingleMesh);
                // Without a scene output there is no callback, so each new asset is serialized once.
                Action<Mesh[]> apply = combiner.CreateSceneOutput
                    ? written => MeshSceneOutput.Apply(combiner, written, result)
                    : (Action<Mesh[]>)null;
                Mesh[] saved = MeshAssetTransaction.Save(meshes, paths, combiner.OverwriteExistingAssets, "Combine meshes",
                    apply, combiner.KeepReadable);
                List<string> stale = StaleOutputs(folder, combiner.MeshName, paths);
                if (stale.Count > 0)
                    combiner.LastBuildWarnings = new List<string>(combiner.LastBuildWarnings)
                    {
                        $"Earlier outputs that this bake did not write are still in '{folder}': {string.Join(", ", stale)}. Delete them if nothing references them.",
                    };
                return saved;
            }
        }

        /// <summary>
        /// Output files of this name that the last save did not write, such as <c>name_2.asset</c> after the build went
        /// from three meshes to two, or <c>name.asset</c> after switching from single to per-material output.
        /// </summary>
        private static List<string> StaleOutputs(string folder, string name, List<string> written)
        {
            var stale = new List<string>();
            if (!Directory.Exists(folder)) return stale;
            var pattern = new Regex("^" + Regex.Escape(name) + @"(_\d+)?\.asset$", RegexOptions.IgnoreCase);
            var writtenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in written) writtenNames.Add(Path.GetFileName(path));
            foreach (string file in Directory.GetFiles(folder, "*.asset"))
            {
                string fileName = Path.GetFileName(file);
                if (pattern.IsMatch(fileName) && !writtenNames.Contains(fileName)) stale.Add(fileName);
            }
            stale.Sort(StringComparer.OrdinalIgnoreCase);
            return stale;
        }

        /// <summary>
        /// Removes the scene output and re-enables the source Renderers this combiner disabled, as one Undo step.
        /// Saved mesh assets are kept.
        /// </summary>
        public static void RestoreSources(this MeshCombiner combiner)
        {
            AssetFileTransaction.RequireEditMode();
            MeshSceneOutput.Restore(combiner);
        }

        /// <summary>
        /// With Combine All Into Single Mesh the output is <c>name.asset</c>; otherwise each material output is
        /// <c>name_0.asset</c>, <c>name_1.asset</c>, ..., even when there is only one.
        /// </summary>
        private static List<string> OutputPaths(string folder, string name, int count, bool singleMesh)
        {
            if (singleMesh && count != 1) throw new ArgumentException("Single-mesh output requires exactly one result.");
            var paths = new List<string>(count);
            for (int i = 0; i < count; i++) paths.Add(singleMesh ? $"{folder}/{name}.asset" : $"{folder}/{name}_{i}.asset");
            return paths;
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
                if (combiner.CollectChildMeshes && filter != null && MeshSceneOutput.IsDisabledByUser(combiner, filter.GetComponent<MeshRenderer>()))
                {
                    skippedDisabledChild = true;
                    continue;
                }
                inputs.Add(filter);
            }
            return inputs.ToArray();
        }
    }
}
