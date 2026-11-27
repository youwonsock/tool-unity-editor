using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Common.OptimizeTool.MeshOptimization.Editor
{
    /// <summary>
    /// The mesh assets the loaded scenes used when Play Mode began, which are the Edit Mode results the saved scenes
    /// reference. No Play Mode save writes them, whichever component or tool saves, so a Play Mode bake cannot silently
    /// change a saved scene. When Play Mode starts, every <c>.asset</c> that a loaded scene's file references (directly or
    /// through its Prefabs) and every <c>.asset</c> mesh a MeshFilter of a loaded scene shows, inactive objects included,
    /// is recorded. The file covers objects whose MeshFilter no longer shows the asset in Play Mode, such as statically
    /// batched ones, or that Awake moved to DontDestroyOnLoad; the MeshFilters cover unsaved changes. A scene loaded
    /// during Play Mode is recorded at the next Play Mode command, before that command changes anything. Other assets, such as the outputs of earlier Play Mode saves, are ordinary outputs
    /// and follow the usual overwrite rule. The record lives in
    /// <see cref="SessionState"/>, so a domain reload during Play Mode, such as a script change with Recompile And Continue
    /// Playing, keeps it; it is cleared when Play Mode ends.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayModeMeshGuard
    {
        private const string StateKey = "Common.OptimizeTool.MeshOptimization.PlayModeMeshGuard";

        static PlayModeMeshGuard() => EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode) RecordLoadedScenes();
            else if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
                SessionState.EraseString(StateKey);
        };

        /// <summary>Records the meshes of every loaded scene not recorded yet in this Play session.</summary>
        internal static void RecordLoadedScenes()
        {
            Record record = Load();
            var meshes = new HashSet<Mesh>();
            var filters = new List<MeshFilter>();
            var paths = new HashSet<string>(record.Paths, StringComparer.OrdinalIgnoreCase);
            bool added = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || record.Scenes.Contains(scene.handle)) continue;
                record.Scenes.Add(scene.handle);
                added = true;
                if (!string.IsNullOrEmpty(scene.path))
                    foreach (string dependency in AssetDatabase.GetDependencies(scene.path, true)) AddAsset(record, paths, dependency);
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    root.GetComponentsInChildren(true, filters);
                    foreach (MeshFilter filter in filters)
                        if (filter.sharedMesh != null) meshes.Add(filter.sharedMesh);
                }
            }
            if (!added) return;
            foreach (Mesh mesh in meshes) AddAsset(record, paths, AssetDatabase.GetAssetPath(mesh));
            SessionState.SetString(StateKey, JsonUtility.ToJson(record));
        }

        /// <summary>Mesh outputs are <c>.asset</c> files, so other files are not kept.</summary>
        private static void AddAsset(Record record, HashSet<string> paths, string path)
        {
            if (path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) && paths.Add(path)) record.Paths.Add(path);
        }

        /// <summary>
        /// Refuses an output path that a loaded scene showed when it was recorded, whatever it shows now. Records the
        /// scenes loaded since the last command first.
        /// </summary>
        internal static void RequireUnprotected(IEnumerable<string> paths)
        {
            RecordLoadedScenes();
            var protectedPaths = new HashSet<string>(Load().Paths, StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
                if (protectedPaths.Contains(path))
                    throw new InvalidOperationException($"'{path}' is an Edit Mode result that a saved scene uses; replacing it in Play Mode would change that scene. Choose another output folder or name.");
        }

        private static Record Load()
        {
            string json = SessionState.GetString(StateKey, null);
            return string.IsNullOrEmpty(json) ? new Record() : JsonUtility.FromJson<Record>(json);
        }

        [Serializable]
        private sealed class Record
        {
            /// <summary>Handles of the recorded scenes; a scene loaded again gets a new handle.</summary>
            public List<int> Scenes = new List<int>();
            public List<string> Paths = new List<string>();
        }
    }
}
