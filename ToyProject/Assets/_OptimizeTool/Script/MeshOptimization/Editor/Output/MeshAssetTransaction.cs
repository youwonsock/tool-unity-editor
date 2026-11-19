using System;
using System.Collections.Generic;
using System.IO;
using Common.OptimizeTool.Editor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.MeshOptimization.Editor
{
    /// <summary>Saves mesh assets and rolls back the meshes and the Undo-recorded scene changes of a failed save.</summary>
    public static class MeshAssetTransaction
    {
        /// <summary>
        /// Saves <paramref name="staged"/> at <paramref name="paths"/>, then runs <paramref name="apply"/> with the saved
        /// meshes. Existing meshes are overwritten in place so their GUIDs survive. If anything fails, the meshes are
        /// restored. In Edit Mode the call is one Undo step and every Undo-recorded change of <paramref name="apply"/> is
        /// reverted too; in Play Mode nothing is recorded with Undo, so the caller puts back what
        /// <paramref name="apply"/> changed. When <paramref name="apply"/> also writes other asset files, wrap this call
        /// in <see cref="AssetFileTransaction.Execute"/> with their paths.
        /// </summary>
        /// <param name="keepReadable">Keep Read/Write on the saved meshes. Off frees their CPU copy in Players.</param>
        public static Mesh[] Save(IList<Mesh> staged, IList<string> paths, bool overwrite, string undoName,
            Action<Mesh[]> apply = null, bool keepReadable = false)
        {
            bool undo = MeshSceneEdit.CurrentIsPersistent;
            var save = new Transaction(staged, paths, overwrite) { KeepReadable = keepReadable, RecordsUndo = undo };
            if (undo)
            {
                Undo.IncrementCurrentGroup();
                save.UndoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(undoName);
            }
            try
            {
                save.Write(undoName, apply != null);
                apply?.Invoke(save.Saved);
                foreach (Mesh mesh in save.Saved) AssetDatabase.SaveAssetIfDirty(mesh);
                if (undo) Undo.CollapseUndoOperations(save.UndoGroup);
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
            private readonly List<string> _created = new List<string>();

            internal Mesh[] Saved { get; }
            internal int UndoGroup { get; set; }
            internal bool KeepReadable { get; set; }
            internal bool RecordsUndo { get; set; }

            /// <summary>Checks every path before the first write.</summary>
            internal Transaction(IList<Mesh> staged, IList<string> paths, bool overwrite)
            {
                if (staged.Count == 0 || staged.Count != paths.Count) throw new ArgumentException("Mesh and path counts must match.");
                foreach (Mesh mesh in staged)
                    if (mesh == null || mesh.vertexCount == 0) throw new ArgumentException("Cannot save an empty mesh.");
                _staged = staged;
                _paths = AssetFileTransaction.ValidateAssetPaths(paths);
                _existing = new Mesh[staged.Count];
                _backups = new Mesh[staged.Count];
                Saved = new Mesh[staged.Count];
                for (int i = 0; i < _paths.Length; i++)
                {
                    if (!_paths[i].EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Mesh outputs need .asset extensions.");
                    _existing[i] = (Mesh)AssetFileTransaction.ValidateOutputTarget(_paths[i], typeof(Mesh), overwrite);
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
                if (RecordsUndo) Undo.RegisterCompleteObjectUndo(_existing[i], undoName);
                EditorUtility.CopySerialized(_staged[i], _existing[i]);
                Saved[i] = _existing[i];
                PrepareMesh(Saved[i], _paths[i], KeepReadable);
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
                    PrepareMesh(Saved[i], _paths[i], KeepReadable);
                    _created.Add(_paths[i]);
                    AssetDatabase.CreateAsset(Saved[i], _paths[i]);
                }
                catch { if (!AssetDatabase.Contains(Saved[i])) Object.DestroyImmediate(Saved[i]); throw; }
                if (AssetDatabase.LoadAssetAtPath<Mesh>(_paths[i]) != Saved[i])
                    throw new IOException($"Failed to create '{_paths[i]}'.");
                // The callback may change the saved mesh; the final save writes those changes.
                if (hasApply) EditorUtility.SetDirty(Saved[i]);
            }

            /// <summary>Restores everything this save changed. Returns the original error followed by any rollback error.</summary>
            internal List<Exception> Rollback(Exception saveError)
            {
                var errors = new List<Exception> { saveError };
                void Attempt(Action restore)
                {
                    try { restore(); }
                    catch (Exception error) { errors.Add(error); }
                }
                if (RecordsUndo) Attempt(() => Undo.RevertAllDownToGroup(UndoGroup));
                for (int i = 0; i < _existing.Length; i++)
                    if (_existing[i] != null && _backups[i] != null)
                    {
                        int index = i;
                        Attempt(() =>
                        {
                            EditorUtility.CopySerialized(_backups[index], _existing[index]);
                            EditorUtility.SetDirty(_existing[index]);
                        });
                    }
                foreach (string path in _created)
                    Attempt(() =>
                    {
                        if (File.Exists(path) && !AssetDatabase.DeleteAsset(path))
                            throw new IOException($"Cannot remove incomplete mesh asset '{path}'.");
                    });
                foreach (Mesh mesh in _existing)
                    if (mesh != null) Attempt(() => AssetDatabase.SaveAssetIfDirty(mesh));
                return errors;
            }

            internal void ReleaseBackups()
            {
                foreach (Mesh backup in _backups) if (backup != null) Object.DestroyImmediate(backup);
            }
        }

        private static void PrepareMesh(Mesh mesh, string path, bool keepReadable)
        {
            mesh.name = Path.GetFileNameWithoutExtension(path);
            // Set the flag through serialization: the editor keeps the CPU geometry needed to write the asset,
            // while UploadMeshData(true) could discard it.
            var serialized = new SerializedObject(mesh);
            SerializedProperty readable = serialized.FindProperty("m_IsReadable");
            if (readable != null && readable.boolValue != keepReadable)
            { readable.boolValue = keepReadable; serialized.ApplyModifiedPropertiesWithoutUndo(); }
        }
    }
}
