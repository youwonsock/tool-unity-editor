using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool
{
    /// <summary>
    /// Shared editor asset output rules: path validation, folder creation, in-place replacement
    /// that keeps GUIDs, and file rollback for builders that declare every output before writing.
    /// </summary>
    public static class AssetFileTransaction
    {
        private static readonly char[] ForbiddenNameChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

        /// <summary>Returns the normalized folder path; it must be under Assets/ and every part a valid name.</summary>
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
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith(".", StringComparison.Ordinal)
                || name.Contains("..") || name.IndexOfAny(ForbiddenNameChars) >= 0
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException($"Use a simple, valid asset file name: '{name}'.");
        }

        /// <summary>Creates every missing folder of a path already accepted by <see cref="ValidateFolder"/>.</summary>
        public static void EnsureFolder(string folder)
        {
            string current = "Assets";
            string[] parts = folder.Split('/');
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next) && string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, parts[i])))
                    throw new IOException($"Cannot create folder '{next}'.");
                current = next;
            }
        }

        /// <summary>
        /// Saves a staged object at path. An existing asset is overwritten in place so its GUID and references
        /// survive, and the staged object is destroyed. Returns the persistent object.
        /// </summary>
        public static T CreateOrReplace<T>(T staged, string path) where T : Object
        {
            if (staged == null) throw new ArgumentNullException(nameof(staged));
            T existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(staged, existing);
                Object.DestroyImmediate(staged);
                staged = existing;
            }
            else AssetDatabase.CreateAsset(staged, path);
            AssetDatabase.SaveAssetIfDirty(staged);
            return staged;
        }

        public static void Execute(IReadOnlyList<string> paths, Action apply)
        {
            if (paths == null || apply == null) throw new ArgumentNullException();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var backups = new List<AssetFileSnapshot>();
            foreach (string path in paths)
            {
                if (!unique.Add(path.Replace('\\', '/'))) throw new ArgumentException("Duplicate asset output.");
                backups.Add(new AssetFileSnapshot(path));
            }
            try { apply(); }
            catch (Exception saveError)
            {
                var errors = new List<Exception> { saveError };
                foreach (AssetFileSnapshot backup in backups) AssetFileSnapshot.TryRestore(backup.Restore, errors);
                if (errors.Count > 1) throw new AggregateException("Asset save and rollback failed.", errors);
                throw;
            }
        }
    }

    /// <summary>Disk snapshots for explicitly declared companion assets, including their GUID metadata.</summary>
    internal sealed class AssetFileSnapshot
    {
        private readonly string _path;
        private readonly byte[] _contents, _meta;

        public AssetFileSnapshot(string path)
        {
            _path = path.Replace('\\', '/');
            AssetFileTransaction.ValidateFolder(Path.GetDirectoryName(_path));
            AssetFileTransaction.ValidateFileName(Path.GetFileName(_path));
            if (Directory.Exists(_path)) throw new IOException($"An asset folder occupies '{_path}'.");
            _contents = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
            _meta = File.Exists(_path + ".meta") ? File.ReadAllBytes(_path + ".meta") : null;
        }

        public void Restore()
        {
            if (_contents == null)
            {
                if (File.Exists(_path) && !AssetDatabase.DeleteAsset(_path))
                    throw new IOException($"Unable to remove incomplete asset: {_path}");
                if (File.Exists(_path + ".meta")) File.Delete(_path + ".meta");
            }
            else
            {
                File.WriteAllBytes(_path, _contents);
            }
            if (_meta != null) File.WriteAllBytes(_path + ".meta", _meta);
            else if (File.Exists(_path + ".meta")) File.Delete(_path + ".meta");
            if (_contents != null)
                AssetDatabase.ImportAsset(_path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }

        internal static void TryRestore(Action action, List<Exception> errors)
        {
            try { action(); }
            catch (Exception error) { errors.Add(error); }
        }
    }
}
