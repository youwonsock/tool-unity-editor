using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Common.OptimizeTool.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.MeshOptimization.Editor
{
    /// <summary>
    /// The editor operations of <see cref="MeshCombiner"/> and <see cref="BackfaceCullMeshBaker"/>. They are extension
    /// methods, so editor code keeps calling <c>combiner.BuildMeshes()</c> while the components hold only settings and
    /// the scene state they own.
    /// </summary>
    public static class MeshToolCommands
    {
        /// <summary>Creates in-memory results only. Dispose the result after saving or inspecting it.</summary>
        public static MeshBuildResult BuildMeshes(this MeshCombiner combiner) => Build(combiner, combiner.CreateSettings());

        private static MeshBuildResult Build(MeshCombiner combiner, MeshBuildSettings settings)
        {
            combiner.LastBuildWarnings = Array.Empty<string>();
            MeshFilter[] sources = CollectSources(combiner, out bool skippedDisabledChild);
            string cameraWarning = null;
            if (settings.Camera == null && (settings.BackfaceCulling || settings.OcclusionCulling))
            {
                // Without a fallback, Validate reports the missing camera.
                settings.Camera = FallbackCamera(combiner.gameObject.scene);
                if (settings.Camera != null) cameraWarning = FallbackCameraWarning(settings.Camera);
            }
            var result = MeshBuildPipeline.Build(sources, combiner.transform.worldToLocalMatrix, settings);
            if (cameraWarning != null) result.AddWarning(cameraWarning);
            if (skippedDisabledChild)
                result.AddWarning("Child MeshFilters with a disabled Renderer were left out. Assign them directly to bake them.");
            combiner.LastBuildWarnings = result.Warnings;
            return result;
        }

        /// <summary>
        /// The view used when no Target Camera is assigned: the enabled camera tagged MainCamera in
        /// <paramref name="scene"/>, otherwise the first enabled camera of that scene in hierarchy order. Null when the
        /// scene has no enabled camera.
        /// </summary>
        private static Camera FallbackCamera(Scene scene)
        {
            Camera first = null;
            var cameras = new List<Camera>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                // Depth-first, which is the hierarchy order.
                root.GetComponentsInChildren(false, cameras);
                foreach (Camera camera in cameras)
                {
                    if (!camera.enabled || !camera.gameObject.activeInHierarchy) continue;
                    if (camera.CompareTag("MainCamera")) return camera;
                    if (first == null) first = camera;
                }
            }
            return first;
        }

        private static string FallbackCameraWarning(Camera camera) => $"No Target Camera; using '{camera.name}'.";

        /// <summary>
        /// Saves the combined meshes as assets and applies the scene output. In Play Mode the scene output is temporary
        /// and not recorded with Undo; the saved assets stay.
        /// </summary>
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
                MeshSceneEdit edit = MeshSceneEdit.Begin();
                // Without a scene output there is no callback, so each new asset is serialized once.
                Action<Mesh[]> apply = combiner.CreateSceneOutput
                    ? written => MeshSceneOutput.Apply(combiner, written, result, edit)
                    : (Action<Mesh[]>)null;
                Mesh[] saved;
                try
                {
                    saved = MeshAssetTransaction.Save(meshes, paths, combiner.OverwriteExistingAssets, "Combine meshes",
                        apply, combiner.KeepReadable);
                }
                catch
                {
                    edit.Revert();
                    throw;
                }
                edit.Commit();
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
        /// Removes the scene output and re-enables the source Renderers this combiner disabled, as one Undo step in
        /// Edit Mode. Saved mesh assets are kept.
        /// </summary>
        public static void RestoreSources(this MeshCombiner combiner)
        {
            MeshSceneEdit edit = MeshSceneEdit.Begin("Restore combined sources");
            // A scene loaded during Play Mode is recorded before Restore removes its Edit Mode result.
            if (!edit.IsPersistent) PlayModeMeshGuard.RecordLoadedScenes();
            try { MeshSceneOutput.Restore(combiner, edit); }
            catch
            {
                edit.Revert();
                throw;
            }
            edit.Commit();
        }

        /// <summary>
        /// Saves, for each target MeshFilter, a copy of its mesh that keeps only the triangles facing the view, as
        /// <c>&lt;MeshFilter name&gt;_BackfaceCulled.asset</c>, and assigns it to that MeshFilter. A MeshFilter that
        /// still shows this baker's output is baked from its recorded original, so baking again does not cut twice; a
        /// mesh assigned by hand since then is the new original. An existing asset at an output path needs the overwrite
        /// setting unless this baker saved it and no MeshFilter outside this bake shows it. Every result is computed
        /// before any asset or MeshFilter changes, and a failed save restores both. In Play Mode the mesh swap is
        /// temporary and not recorded with Undo; the saved assets stay.
        /// </summary>
        public static Mesh[] BakeBackfaces(this BackfaceCullMeshBaker baker)
        {
            MeshSceneEdit edit = MeshSceneEdit.Begin();
            string folder = AssetFileTransaction.ValidateFolder(baker.SavePath);
            float angle = baker.BackfaceCullingAngle;
            MeshBuildSettings.ValidateBackfaceAngle(angle, "Backface Culling Angle");
            Camera camera = null;
            if (baker.UseCustomVector) MeshProcessing.ValidateViewDirection(baker.CustomCameraVector);
            else
            {
                camera = baker.TargetCamera;
                if (camera == null)
                {
                    camera = FallbackCamera(baker.gameObject.scene);
                    if (camera == null) throw new ArgumentException("Assign a Target Camera or use a custom view direction.");
                    Debug.LogWarning(FallbackCameraWarning(camera), baker);
                }
            }
            MeshFilter[] filters = BakeTargets(baker);
            var originals = new Mesh[filters.Length];
            var paths = new string[filters.Length];
            for (int i = 0; i < filters.Length; i++)
            {
                originals[i] = BakeSource(baker, filters[i]);
                paths[i] = $"{folder}/{filters[i].name}_BackfaceCulled.asset";
            }
            var ownOutputs = new HashSet<Mesh>();
            foreach (string path in AssetFileTransaction.ValidateAssetPaths(paths))
            {
                // This baker's own outputs are replaced without the overwrite setting, unless another MeshFilter shows them.
                if (AssetDatabase.LoadMainAssetAtPath(path) is Mesh existing && IsOwnOutput(baker, existing)) ownOutputs.Add(existing);
                else AssetFileTransaction.ValidateOutputTarget(path, typeof(Mesh), baker.OverwriteExistingAssets);
            }
            if (ownOutputs.Count > 0 && !baker.OverwriteExistingAssets) RequireUnshared(baker, filters, ownOutputs);
            var staged = new List<Mesh>();
            try
            {
                for (int i = 0; i < filters.Length; i++)
                {
                    Mesh readable = MeshGeometry.ReadableCopy(originals[i], baker.AllowReadableCopy, baker.AllowNormalRecalculation);
                    try
                    {
                        Mesh culled = camera != null
                            ? MeshGeometry.CullBackfaces(readable, filters[i].transform, camera, angle)
                            : MeshGeometry.CullBackfaces(readable, filters[i].transform, baker.CustomCameraVector, angle);
                        if (culled == null) throw new InvalidOperationException($"Every triangle of '{filters[i].name}' faces away from the view.");
                        staged.Add(culled);
                    }
                    finally { Object.DestroyImmediate(readable); }
                }
                Mesh[] saved;
                try
                {
                    saved = MeshAssetTransaction.Save(staged, paths, true, "Bake backfaces",
                        baked => AssignBaked(baker, filters, originals, baked, edit));
                }
                catch
                {
                    edit.Revert();
                    throw;
                }
                edit.Commit();
                return saved;
            }
            finally { foreach (Mesh mesh in staged) Object.DestroyImmediate(mesh); }
        }

        /// <summary>
        /// Gives every MeshFilter that still shows this baker's output its recorded original mesh and clears the record,
        /// as one Undo step in Edit Mode. A mesh assigned by hand since the bake is kept, and saved baked assets are
        /// kept; a later bake still replaces them without the overwrite setting while no other MeshFilter shows them.
        /// </summary>
        public static void RestoreOriginalMeshes(this BackfaceCullMeshBaker baker)
        {
            MeshSceneEdit edit = MeshSceneEdit.Begin("Restore original meshes");
            // A scene loaded during Play Mode is recorded before the restore takes its Edit Mode result off.
            if (!edit.IsPersistent) PlayModeMeshGuard.RecordLoadedScenes();
            try
            {
                RecordBaker(baker, edit, "Restore original meshes");
                foreach (BakedMeshOriginal record in baker.OriginalMeshes)
                    if (record.Filter != null && record.Original != null && record.Baked != null && record.Filter.sharedMesh == record.Baked)
                        SetMesh(record.Filter, record.Original, edit, "Restore original mesh");
                baker.OriginalMeshes = new List<BakedMeshOriginal>();
                edit.Changed(baker);
            }
            catch
            {
                edit.Revert();
                throw;
            }
            edit.Commit();
        }

        private static MeshFilter[] BakeTargets(BackfaceCullMeshBaker baker)
        {
            MeshFilter[] direct = baker.TargetMeshFilters;
            RequireOneSelection(baker.CollectChildMeshes, direct);
            MeshFilter[] filters = baker.CollectChildMeshes ? baker.GetComponentsInChildren<MeshFilter>() : direct;
            if (filters.Length == 0) throw new ArgumentException("No MeshFilters were found under this object.");
            var seen = new HashSet<MeshFilter>();
            foreach (MeshFilter filter in filters)
            {
                if (filter == null || !seen.Add(filter)) throw new ArgumentException("Target MeshFilters must be present and unique.");
                MeshBuildPipeline.RequireUnbatched(filter);
            }
            return filters;
        }

        /// <summary>The recorded original while the MeshFilter still shows this baker's output; otherwise its current mesh.</summary>
        private static Mesh BakeSource(BackfaceCullMeshBaker baker, MeshFilter filter)
        {
            Mesh current = filter.sharedMesh;
            if (current == null) throw new ArgumentException($"'{filter.name}' has no mesh.");
            foreach (BakedMeshOriginal record in baker.OriginalMeshes)
                if (record.Filter == filter && record.Baked == current && record.Original != null) return record.Original;
            return current;
        }

        private static bool IsOwnOutput(BackfaceCullMeshBaker baker, Mesh mesh) => baker.BakedMeshes.Contains(mesh);

        /// <summary>
        /// Refuses to replace an own output that a MeshFilter outside <paramref name="targets"/> shows, because that
        /// MeshFilter would change too. The saved-mesh list is serialized, so a duplicated baker or another instance of
        /// the same Prefab lists the original's outputs as its own. The check covers every loaded scene, inactive
        /// objects included; in a Prefab stage it covers only that Prefab, whose instances follow it anyway.
        /// </summary>
        private static void RequireUnshared(BackfaceCullMeshBaker baker, MeshFilter[] targets, HashSet<Mesh> outputs)
        {
            var excluded = new HashSet<MeshFilter>(targets);
            var found = new List<MeshFilter>();
            foreach (Scene scene in ScenesInView(baker.gameObject.scene))
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    root.GetComponentsInChildren(true, found);
                    foreach (MeshFilter filter in found)
                    {
                        Mesh shown = filter.sharedMesh;
                        if (shown == null || excluded.Contains(filter) || !outputs.Contains(shown)) continue;
                        throw new InvalidOperationException($"Output already exists: {AssetDatabase.GetAssetPath(shown)}. '{HierarchyPath(filter.transform)}' also shows it and is not a target of this bake. Turn on Overwrite Existing Assets to replace it, or change the Save Path.");
                    }
                }
        }

        /// <summary>The scenes whose objects a bake in <paramref name="own"/> can change through a shared asset.</summary>
        private static IEnumerable<Scene> ScenesInView(Scene own)
        {
            if (!own.IsValid() || !own.isLoaded) yield break;
            yield return own;
            if (EditorSceneManager.IsPreviewScene(own)) yield break;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene != own) yield return scene;
            }
        }

        private static string HierarchyPath(Transform transform)
        {
            string path = transform.name;
            for (Transform parent = transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return path;
        }

        private static void AssignBaked(BackfaceCullMeshBaker baker, MeshFilter[] filters, Mesh[] originals, Mesh[] baked,
            MeshSceneEdit edit)
        {
            RecordBaker(baker, edit, "Record original meshes");
            // Build the new record on the side, like the combiner's ledger, and assign it after the scene changes.
            var records = new List<BakedMeshOriginal>();
            var targets = new HashSet<MeshFilter>(filters);
            foreach (BakedMeshOriginal record in baker.OriginalMeshes)
                if (record.Filter != null && !targets.Contains(record.Filter)) records.Add(record);
            for (int i = 0; i < filters.Length; i++)
            {
                SetMesh(filters[i], baked[i], edit, "Assign baked mesh");
                records.Add(new BakedMeshOriginal { Filter = filters[i], Original = originals[i], Baked = baked[i] });
            }
            var outputs = new List<Mesh>();
            foreach (Mesh mesh in baker.BakedMeshes)
                if (mesh != null && Array.IndexOf(baked, mesh) < 0) outputs.Add(mesh);
            outputs.AddRange(baked);
            baker.OriginalMeshes = records;
            baker.BakedMeshes = outputs;
            edit.Changed(baker);
        }

        /// <summary>Bakes and restores replace both lists as a whole, so keeping the old ones reverts them.</summary>
        private static void RecordBaker(BackfaceCullMeshBaker baker, MeshSceneEdit edit, string undoName)
        {
            List<BakedMeshOriginal> records = baker.OriginalMeshes;
            List<Mesh> outputs = baker.BakedMeshes;
            edit.Record(baker, undoName, () =>
            {
                baker.OriginalMeshes = records;
                baker.BakedMeshes = outputs;
            });
        }

        private static void SetMesh(MeshFilter filter, Mesh mesh, MeshSceneEdit edit, string undoName)
        {
            Mesh previous = filter.sharedMesh;
            edit.Record(filter, undoName, () =>
            {
                if (filter != null) filter.sharedMesh = previous;
            });
            filter.sharedMesh = mesh;
            edit.Changed(filter);
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
            AssetFileTransaction.RequireStablePlayMode();
            MeshFilter[] direct = combiner.TargetMeshFilters;
            RequireOneSelection(combiner.CollectChildMeshes, direct);
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

        /// <summary>A component takes its inputs from exactly one of the assigned list and child collection.</summary>
        private static void RequireOneSelection(bool collectChildMeshes, MeshFilter[] direct)
        {
            bool hasDirect = direct != null && direct.Length > 0;
            if (collectChildMeshes && hasDirect)
                throw new ArgumentException("Choose direct MeshFilters or child collection, not both.");
            if (!collectChildMeshes && !hasDirect)
                throw new ArgumentException("Assign target MeshFilters or enable child collection.");
        }
    }
}
