#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool
{
    public sealed class MeshSaveOptions
    {
        public string Folder { get; }
        public string Name { get; }
        public bool Overwrite { get; }
        public MeshSaveOptions(string folder, string name, bool overwrite = false)
        {
            Folder = MeshAssetTransaction.ValidateFolder(folder);
            MeshAssetTransaction.ValidateFileName(name);
            Name = name; Overwrite = overwrite;
        }
        public List<string> CreatePaths(int count, bool singleMesh)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (singleMesh && count != 1) throw new ArgumentException("Single-mesh output requires exactly one result.");
            var paths = new List<string>(count);
            for (int i = 0; i < count; i++) paths.Add($"{Folder}/{Name}{(singleMesh ? "" : "_" + i)}.asset");
            return paths;
        }
    }

    /// <summary>Rolls back mesh assets, declared companion asset files, and Undo-recorded scene changes.</summary>
    public static class MeshAssetTransaction
    {
        public static string ValidateFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("An output folder is required.");
            string path = folder.Replace('\\', '/').TrimEnd('/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal)) throw new ArgumentException("Output must be under Assets/.");
            foreach (string part in path.Split('/')) ValidateFileName(part);
            return path;
        }

        public static void ValidateFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name.Contains("..")
                || name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Use a simple, valid asset file name.");
        }

        public static Mesh[] Save(IList<Mesh> staged, IList<string> paths, bool overwrite, string undoName,
            Action<Mesh[]> apply = null, IReadOnlyList<string> companionAssetPaths = null)
        {
            MeshProcessingUtility.RequireEditMode();
            if (staged.Count == 0 || staged.Count != paths.Count) throw new ArgumentException("Mesh and path counts must match.");
            var existing = new Mesh[staged.Count];
            var normalized = new string[paths.Count];
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < paths.Count; i++)
            {
                if (staged[i] == null || staged[i].vertexCount == 0) throw new ArgumentException("Cannot save an empty mesh.");
                string path = paths[i].Replace('\\', '/');
                string folder = ValidateFolder(Path.GetDirectoryName(path));
                string file = Path.GetFileName(path);
                ValidateFileName(file);
                if (!file.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Mesh outputs need .asset extensions.");
                normalized[i] = folder + "/" + file;
                if (!unique.Add(normalized[i])) throw new ArgumentException("Several outputs resolve to the same asset.");
                Object asset = AssetDatabase.LoadMainAssetAtPath(normalized[i]);
                if (asset == null && File.Exists(normalized[i]))
                    throw new InvalidOperationException($"An unrecognized file occupies '{path}'.");
                if (asset != null && !(asset is Mesh)) throw new InvalidOperationException($"A non-mesh asset occupies '{path}'.");
                if (asset != null && !overwrite) throw new InvalidOperationException($"Asset already exists: {path}");
                existing[i] = asset as Mesh;
            }

            var companions = new List<AssetFileSnapshot>();
            if (companionAssetPaths != null)
                foreach (string path in companionAssetPaths)
                {
                    string normalizedPath = path.Replace('\\', '/');
                    if (!unique.Add(normalizedPath)) throw new ArgumentException("An asset was declared more than once.");
                    companions.Add(new AssetFileSnapshot(normalizedPath));
                }
            var backups = new Mesh[staged.Count];
            var saved = new Mesh[staged.Count];
            var created = new List<string>();
            var ensuredFolders = new HashSet<string>(StringComparer.Ordinal);
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            try
            {
                // Snapshot every existing output before the first write.
                for (int i = 0; i < existing.Length; i++)
                    if (existing[i] != null)
                    {
                        backups[i] = Object.Instantiate(existing[i]);
                        backups[i].name = existing[i].name;
                    }
                for (int i = 0; i < staged.Count; i++)
                {
                    string folder = Path.GetDirectoryName(normalized[i]).Replace('\\', '/');
                    if (ensuredFolders.Add(folder)) EnsureFolder(folder);
                    if (existing[i] != null)
                    {
                        Undo.RegisterCompleteObjectUndo(existing[i], undoName);
                        EditorUtility.CopySerialized(staged[i], existing[i]);
                        saved[i] = existing[i];
                        PrepareMesh(saved[i], normalized[i]);
                        EditorUtility.SetDirty(saved[i]);
                    }
                    else
                    {
                        // CreateAsset writes immediately. Finalize metadata first, so a new asset
                        // without an apply callback needs only one geometry serialization.
                        saved[i] = Object.Instantiate(staged[i]);
                        // Track before CreateAsset so rollback also handles a partial CreateAsset failure.
                        try
                        {
                            PrepareMesh(saved[i], normalized[i]);
                            created.Add(normalized[i]);
                            AssetDatabase.CreateAsset(saved[i], normalized[i]);
                        }
                        catch { if (!AssetDatabase.Contains(saved[i])) Object.DestroyImmediate(saved[i]); throw; }
                        if (AssetDatabase.LoadAssetAtPath<Mesh>(normalized[i]) != saved[i])
                            throw new IOException($"Failed to create '{normalized[i]}'.");
                        // Preserve the public callback's ability to mutate the saved Mesh and have
                        // those changes committed. It sees complete geometry on disk as before.
                        if (apply != null) EditorUtility.SetDirty(saved[i]);
                    }
                }
                apply?.Invoke(saved);
                foreach (Mesh mesh in saved) AssetDatabase.SaveAssetIfDirty(mesh);
                Undo.CollapseUndoOperations(group);
                return saved;
            }
            catch (Exception saveError)
            {
                var errors = new List<Exception> { saveError };
                AssetFileSnapshot.TryRestore(() => Undo.RevertAllDownToGroup(group), errors);
                for (int i = 0; i < existing.Length; i++)
                    if (existing[i] != null && backups[i] != null)
                    {
                        int index = i;
                        AssetFileSnapshot.TryRestore(() =>
                        {
                            EditorUtility.CopySerialized(backups[index], existing[index]);
                            EditorUtility.SetDirty(existing[index]);
                        }, errors);
                    }
                foreach (string path in created)
                    AssetFileSnapshot.TryRestore(() =>
                    {
                        if (File.Exists(path) && !AssetDatabase.DeleteAsset(path))
                            throw new IOException($"Cannot remove incomplete mesh asset '{path}'.");
                    }, errors);
                foreach (Mesh mesh in existing)
                    if (mesh != null) AssetFileSnapshot.TryRestore(() => AssetDatabase.SaveAssetIfDirty(mesh), errors);
                // Restore companions after meshes, so imported prefabs resolve the restored mesh references.
                foreach (AssetFileSnapshot companion in companions)
                    AssetFileSnapshot.TryRestore(companion.Restore, errors);
                if (errors.Count > 1) throw new AggregateException("Mesh save and rollback failed.", errors);
                throw;
            }
            finally { foreach (Mesh backup in backups) if (backup != null) Object.DestroyImmediate(backup); }
        }

        private static void PrepareMesh(Mesh mesh, string path)
        {
            mesh.name = Path.GetFileNameWithoutExtension(path);
            // Keep CPU geometry for serialization; UploadMeshData(true) can discard it.
            var serialized = new SerializedObject(mesh);
            SerializedProperty readable = serialized.FindProperty("m_IsReadable");
            if (readable != null && readable.boolValue)
            { readable.boolValue = false; serialized.ApplyModifiedPropertiesWithoutUndo(); }
        }

        private static void EnsureFolder(string path)
        {
            string current = "Assets";
            string[] parts = path.Split('/');
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next) && string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, parts[i])))
                    throw new IOException($"Cannot create folder '{next}'.");
                current = next;
            }
        }
    }
}
#endif
