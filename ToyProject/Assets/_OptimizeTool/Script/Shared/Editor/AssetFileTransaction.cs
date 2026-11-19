using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.Editor
{
    /// <summary>
    /// Shared editor asset output rules: path validation, folder creation, in-place replacement
    /// that keeps GUIDs, and file rollback for builders that declare every output before writing.
    /// </summary>
    public static class AssetFileTransaction
    {
        private static readonly char[] ForbiddenNameChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

        /// <summary>For editor commands that run in Edit Mode only, such as the sample builders.</summary>
        public static void RequireEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Run this in Edit Mode.");
        }

        /// <summary>
        /// For editor commands that run in Edit Mode and Play Mode: fails while Play Mode is starting or stopping, when
        /// the scene is about to be replaced.
        /// </summary>
        public static void RequireStablePlayMode()
        {
            if (EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Wait until Play Mode has finished starting or stopping.");
        }

        /// <summary>
        /// Checks an output path before anything is written and returns the asset that will be replaced, or null.
        /// A folder, an unrecognized file or an asset of another type always fails; an asset of the expected
        /// type fails unless <paramref name="overwrite"/> is set.
        /// </summary>
        public static Object ValidateOutputTarget(string path, Type type, bool overwrite)
        {
            path = ValidateAssetPath(path);
            if (Directory.Exists(path)) throw new InvalidOperationException($"A folder occupies the output path: {path}");
            Object existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing == null)
            {
                if (File.Exists(path)) throw new InvalidOperationException($"An unrecognized file occupies the output path: {path}");
                return null;
            }
            if (existing.GetType() != type)
                throw new InvalidOperationException($"An incompatible {existing.GetType().Name} occupies the output path: {path}");
            if (!overwrite) throw new InvalidOperationException($"Output already exists: {path}");
            return existing;
        }

        /// <summary>Returns the normalized folder path; it must be under Assets/ and every part a valid name.</summary>
        public static string ValidateFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("An output folder is required.");
            string path = folder.Replace('\\', '/').TrimEnd('/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal)) throw new ArgumentException("Output must be under Assets/.");
            foreach (string part in path.Split('/')) ValidateFileName(part);
            return path;
        }

        /// <summary>
        /// Returns the normalized path of an asset file. Its folder must pass <see cref="ValidateFolder"/> and its file
        /// name <see cref="ValidateFileName"/>.
        /// </summary>
        public static string ValidateAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An asset path is required.");
            string normalized = path.Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            string folder = ValidateFolder(slash < 0 ? string.Empty : normalized.Substring(0, slash));
            string file = normalized.Substring(slash + 1);
            ValidateFileName(file);
            return folder + "/" + file;
        }

        /// <summary>Validates every path with <see cref="ValidateAssetPath"/> and rejects a list that names one asset twice.</summary>
        public static string[] ValidateAssetPaths(IEnumerable<string> paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            var normalized = new List<string>();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                string asset = ValidateAssetPath(path);
                if (!unique.Add(asset)) throw new ArgumentException($"An asset output is listed more than once: {asset}");
                normalized.Add(asset);
            }
            return normalized.ToArray();
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

        /// <summary>
        /// Runs <paramref name="apply"/>, which writes the asset files at <paramref name="paths"/>. If it throws, every
        /// file and its .meta (so its GUID) is restored to what it was before the call, and a file that did not exist
        /// is removed. The original error is rethrown, together with any restore error.
        /// </summary>
        public static void Execute(IEnumerable<string> paths, Action apply)
        {
            if (apply == null) throw new ArgumentNullException(nameof(apply));
            var backups = new List<AssetFileSnapshot>();
            foreach (string path in ValidateAssetPaths(paths)) backups.Add(new AssetFileSnapshot(path));
            try { apply(); }
            catch (Exception saveError)
            {
                var errors = new List<Exception> { saveError };
                foreach (AssetFileSnapshot backup in backups)
                {
                    try { backup.Restore(); }
                    catch (Exception restoreError) { errors.Add(restoreError); }
                }
                if (errors.Count > 1) throw new AggregateException("Asset save and rollback failed.", errors);
                throw;
            }
        }

        /// <summary>The bytes of one asset file and its .meta before <see cref="Execute"/> ran.</summary>
        private sealed class AssetFileSnapshot
        {
            private readonly string _path;
            private readonly byte[] _contents, _meta;

            internal AssetFileSnapshot(string path)
            {
                _path = path;
                if (Directory.Exists(_path)) throw new IOException($"An asset folder occupies '{_path}'.");
                _contents = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
                _meta = File.Exists(_path + ".meta") ? File.ReadAllBytes(_path + ".meta") : null;
            }

            internal void Restore()
            {
                if (_contents == null)
                {
                    if (File.Exists(_path) && !AssetDatabase.DeleteAsset(_path))
                        throw new IOException($"Unable to remove incomplete asset: {_path}");
                }
                else File.WriteAllBytes(_path, _contents);
                if (_meta != null) File.WriteAllBytes(_path + ".meta", _meta);
                else if (File.Exists(_path + ".meta")) File.Delete(_path + ".meta");
                if (_contents != null)
                    AssetDatabase.ImportAsset(_path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
        }
    }
}
