using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.MeshOptimization
{
    /// <summary>Rolls back mesh assets, declared companion asset files, and Undo-recorded scene changes.</summary>
    public static class MeshAssetTransaction
    {
        /// <summary>
        /// Saves <paramref name="staged"/> at <paramref name="paths"/>, then runs <paramref name="apply"/> with the saved
        /// meshes. Existing meshes are overwritten in place so their GUIDs survive. If anything fails, the meshes, the
        /// declared companion files and every Undo-recorded change of this call are restored.
        /// </summary>
        public static Mesh[] Save(IList<Mesh> staged, IList<string> paths, bool overwrite, string undoName,
            Action<Mesh[]> apply = null, IReadOnlyList<string> companionAssetPaths = null)
        {
            AssetFileTransaction.RequireEditMode();
            var save = new Transaction(staged, paths, overwrite, companionAssetPaths);
            Undo.IncrementCurrentGroup();
            save.UndoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            try
            {
                save.Write(undoName, apply != null);
                apply?.Invoke(save.Saved);
                foreach (Mesh mesh in save.Saved) AssetDatabase.SaveAssetIfDirty(mesh);
                Undo.CollapseUndoOperations(save.UndoGroup);
                return save.Saved;
            }
            catch (Exception saveError)
            {
                List<Exception> errors = save.Rollback(saveError);
                if (errors.Count > 1) throw new AggregateException("Mesh save and rollback failed.", errors);
                throw;
            }
            finally { save.ReleaseBackups(); }
        }

        /// <summary>The validated targets of one save and what has been written so far.</summary>
        private sealed class Transaction
        {
            private readonly IList<Mesh> _staged;
            private readonly string[] _paths;
            private readonly Mesh[] _existing;
            private readonly Mesh[] _backups;
            private readonly List<AssetFileSnapshot> _companions = new List<AssetFileSnapshot>();
            private readonly List<string> _created = new List<string>();

            internal Mesh[] Saved { get; }
            internal int UndoGroup { get; set; }

            /// <summary>Checks every path before the first write.</summary>
            internal Transaction(IList<Mesh> staged, IList<string> paths, bool overwrite, IReadOnlyList<string> companionPaths)
            {
                if (staged.Count == 0 || staged.Count != paths.Count) throw new ArgumentException("Mesh and path counts must match.");
                _staged = staged;
                _paths = new string[paths.Count];
                _existing = new Mesh[staged.Count];
                _backups = new Mesh[staged.Count];
                Saved = new Mesh[staged.Count];
                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < paths.Count; i++)
                {
                    if (staged[i] == null || staged[i].vertexCount == 0) throw new ArgumentException("Cannot save an empty mesh.");
                    string path = paths[i].Replace('\\', '/');
                    string folder = AssetFileTransaction.ValidateFolder(Path.GetDirectoryName(path));
                    string file = Path.GetFileName(path);
                    AssetFileTransaction.ValidateFileName(file);
                    if (!file.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Mesh outputs need .asset extensions.");
                    _paths[i] = folder + "/" + file;
                    if (!unique.Add(_paths[i])) throw new ArgumentException("Several outputs resolve to the same asset.");
                    _existing[i] = (Mesh)AssetFileTransaction.ValidateOutputTarget(_paths[i], typeof(Mesh), overwrite);
                }
                if (companionPaths != null)
                    foreach (string path in companionPaths)
                    {
                        string normalized = path.Replace('\\', '/');
                        if (!unique.Add(normalized)) throw new ArgumentException("An asset was declared more than once.");
                        _companions.Add(new AssetFileSnapshot(normalized));
                    }
            }

            internal void Write(string undoName, bool hasApply)
            {
                // Snapshot every existing output before the first write.
                for (int i = 0; i < _existing.Length; i++)
                    if (_existing[i] != null)
                    {
                        _backups[i] = Object.Instantiate(_existing[i]);
                        _backups[i].name = _existing[i].name;
                    }
                var ensuredFolders = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < _staged.Count; i++)
                {
                    string folder = Path.GetDirectoryName(_paths[i]).Replace('\\', '/');
                    if (ensuredFolders.Add(folder)) AssetFileTransaction.EnsureFolder(folder);
                    if (_existing[i] != null) Overwrite(i, undoName);
                    else Create(i, hasApply);
                }
            }

            private void Overwrite(int i, string undoName)
            {
                Undo.RegisterCompleteObjectUndo(_existing[i], undoName);
                EditorUtility.CopySerialized(_staged[i], _existing[i]);
                Saved[i] = _existing[i];
                PrepareMesh(Saved[i], _paths[i]);
                EditorUtility.SetDirty(Saved[i]);
            }

            private void Create(int i, bool hasApply)
            {
                // CreateAsset writes immediately. Finalize metadata first, so a new asset
                // without an apply callback needs only one geometry serialization.
                Saved[i] = Object.Instantiate(_staged[i]);
                // Track before CreateAsset so rollback also handles a partial CreateAsset failure.
                try
                {
                    PrepareMesh(Saved[i], _paths[i]);
                    _created.Add(_paths[i]);
                    AssetDatabase.CreateAsset(Saved[i], _paths[i]);
                }
                catch { if (!AssetDatabase.Contains(Saved[i])) Object.DestroyImmediate(Saved[i]); throw; }
                if (AssetDatabase.LoadAssetAtPath<Mesh>(_paths[i]) != Saved[i])
                    throw new IOException($"Failed to create '{_paths[i]}'.");
                // Preserve the callback's ability to mutate the saved Mesh and have those changes
                // committed. It sees complete geometry on disk as before.
                if (hasApply) EditorUtility.SetDirty(Saved[i]);
            }

            /// <summary>Restores everything this save changed. Returns the original error followed by any rollback error.</summary>
            internal List<Exception> Rollback(Exception saveError)
            {
                var errors = new List<Exception> { saveError };
                AssetFileSnapshot.TryRestore(() => Undo.RevertAllDownToGroup(UndoGroup), errors);
                for (int i = 0; i < _existing.Length; i++)
                    if (_existing[i] != null && _backups[i] != null)
                    {
                        int index = i;
                        AssetFileSnapshot.TryRestore(() =>
                        {
                            EditorUtility.CopySerialized(_backups[index], _existing[index]);
                            EditorUtility.SetDirty(_existing[index]);
                        }, errors);
                    }
                foreach (string path in _created)
                    AssetFileSnapshot.TryRestore(() =>
                    {
                        if (File.Exists(path) && !AssetDatabase.DeleteAsset(path))
                            throw new IOException($"Cannot remove incomplete mesh asset '{path}'.");
                    }, errors);
                foreach (Mesh mesh in _existing)
                    if (mesh != null) AssetFileSnapshot.TryRestore(() => AssetDatabase.SaveAssetIfDirty(mesh), errors);
                // Restore companions after meshes, so imported prefabs resolve the restored mesh references.
                foreach (AssetFileSnapshot companion in _companions)
                    AssetFileSnapshot.TryRestore(companion.Restore, errors);
                return errors;
            }

            internal void ReleaseBackups()
            {
                foreach (Mesh backup in _backups) if (backup != null) Object.DestroyImmediate(backup);
            }
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
    }
}
