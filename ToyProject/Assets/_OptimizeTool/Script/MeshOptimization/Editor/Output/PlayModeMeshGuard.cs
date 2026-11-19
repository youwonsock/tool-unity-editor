using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.MeshOptimization.Editor
{
    /// <summary>
    /// The mesh assets each component's scene result showed when Play Mode began, which is the Edit Mode result the
    /// saved scene references. Play Mode commands never write them, so they cannot silently change the saved scene.
    /// Assets a Play Mode command writes during the session are ordinary outputs. The record is kept per component from
    /// its first Play Mode command, before anything replaces its result, and is cleared when Play Mode ends.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayModeMeshGuard
    {
        private static readonly Dictionary<int, HashSet<string>> Protected = new Dictionary<int, HashSet<string>>();

        static PlayModeMeshGuard() => EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
                Protected.Clear();
        };

        /// <summary>
        /// Records the assets <paramref name="shownBy"/> shows as the protected set of <paramref name="owner"/>, unless
        /// it has one from an earlier command of this Play session.
        /// </summary>
        internal static void Capture(Object owner, IEnumerable<MeshFilter> shownBy)
        {
            if (Protected.ContainsKey(owner.GetInstanceID())) return;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MeshFilter filter in shownBy)
            {
                string path = filter != null ? AssetDatabase.GetAssetPath(filter.sharedMesh) : null;
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }
            Protected.Add(owner.GetInstanceID(), paths);
        }

        /// <summary>Refuses an output path in the protected set of <paramref name="owner"/>, whatever its result shows now.</summary>
        internal static void RequireUnprotected(Object owner, IEnumerable<string> paths)
        {
            if (!Protected.TryGetValue(owner.GetInstanceID(), out HashSet<string> protectedPaths)) return;
            foreach (string path in paths)
                if (protectedPaths.Contains(path))
                    throw new InvalidOperationException($"'{path}' is the Edit Mode result the saved scene uses; replacing it in Play Mode would change the saved scene. Choose another output folder or name.");
        }
    }
}
