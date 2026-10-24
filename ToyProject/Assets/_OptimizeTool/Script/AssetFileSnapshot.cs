#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace Common.OptimizeTool
{
    /// <summary>File rollback for isolated editor builders that declare every output before writing.</summary>
    public static class AssetFileTransaction
    {
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
        internal long ContentBytes => _contents?.LongLength ?? 0L;
        // Keep callers' payload-byte metric unchanged; metadata is retained for recovery too.

        public AssetFileSnapshot(string path)
        {
            _path = path.Replace('\\', '/');
            MeshAssetTransaction.ValidateFolder(Path.GetDirectoryName(_path));
            MeshAssetTransaction.ValidateFileName(Path.GetFileName(_path));
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
#endif
