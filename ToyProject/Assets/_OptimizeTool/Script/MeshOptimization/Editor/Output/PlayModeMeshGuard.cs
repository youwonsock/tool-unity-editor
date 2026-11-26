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
    /// its first Play Mode command, before anything replaces its result, and is cleared when Play Mode ends. It lives in
    /// <see cref="SessionState"/>, so a domain reload during Play Mode, such as a script change with Recompile And
    /// Continue Playing, keeps it; instance IDs survive the reload.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayModeMeshGuard
    {
        private const string StateKey = "Common.OptimizeTool.MeshOptimization.PlayModeMeshGuard";

        static PlayModeMeshGuard() => EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
                SessionState.EraseString(StateKey);
        };

        /// <summary>
        /// Records the assets <paramref name="shownBy"/> shows as the protected set of <paramref name="owner"/>, unless
        /// it has one from an earlier command of this Play session.
        /// </summary>
        internal static void Capture(Object owner, IEnumerable<MeshFilter> shownBy)
        {
            Record record = Load();
            int id = owner.GetInstanceID();
            if (record.Find(id) != null) return;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MeshFilter filter in shownBy)
            {
                string path = filter != null ? AssetDatabase.GetAssetPath(filter.sharedMesh) : null;
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }
            record.Owners.Add(new OwnerPaths { Owner = id, Paths = new List<string>(paths) });
            SessionState.SetString(StateKey, JsonUtility.ToJson(record));
        }

        /// <summary>Refuses an output path in the protected set of <paramref name="owner"/>, whatever its result shows now.</summary>
        internal static void RequireUnprotected(Object owner, IEnumerable<string> paths)
        {
            OwnerPaths owned = Load().Find(owner.GetInstanceID());
            if (owned == null) return;
            var protectedPaths = new HashSet<string>(owned.Paths, StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
                if (protectedPaths.Contains(path))
                    throw new InvalidOperationException($"'{path}' is the Edit Mode result the saved scene uses; replacing it in Play Mode would change the saved scene. Choose another output folder or name.");
        }

        private static Record Load()
        {
            string json = SessionState.GetString(StateKey, null);
            return string.IsNullOrEmpty(json) ? new Record() : JsonUtility.FromJson<Record>(json);
        }

        [Serializable]
        private sealed class Record
        {
            public List<OwnerPaths> Owners = new List<OwnerPaths>();

            public OwnerPaths Find(int owner) => Owners.Find(entry => entry.Owner == owner);
        }

        [Serializable]
        private sealed class OwnerPaths
        {
            public int Owner;
            public List<string> Paths = new List<string>();
        }
    }
}
