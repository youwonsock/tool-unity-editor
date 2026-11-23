using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>The AssetDatabase side of a bake: where it writes, its folders and its checks.</summary>
    internal static class FlowFieldBakeAssetUtility
    {
        // AssetDatabase reports a failed write only in the Console. The Asset then
        // stays dirty, and a bake must not report success over the old file.
        internal static void Save(UnityEngine.Object asset)
        {
            AssetDatabase.SaveAssetIfDirty(asset);
            if (EditorUtility.IsDirty(asset))
                throw new IOException($"Could not save {AssetDatabase.GetAssetPath(asset)}.");
        }

        /// <summary>
        /// The Manager's identity in its saved scene, which a bake records as
        /// the owner of the asset it writes. A duplicated Manager has its own.
        /// </summary>
        internal static string OwnerId(FlowFieldManager manager)
        {
            if (!manager.gameObject.scene.IsValid() || string.IsNullOrEmpty(manager.gameObject.scene.path))
                throw new InvalidOperationException("Save the scene before baking.");
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(manager);
            if (id.targetObjectId == 0)
                throw new InvalidOperationException("Save the scene before baking.");
            return id.ToString();
        }

        /// <summary>
        /// Where a bake writes: the assigned asset when this Manager baked it,
        /// or else this Manager's stable path next to its scene. An asset that
        /// another Manager baked (a duplicated Manager keeps the reference) is
        /// never written.
        /// </summary>
        internal static string ResolveAssetPath(
            FlowFieldManager manager,
            FlowFieldBakeAsset assigned,
            string ownerId,
            string suffix)
        {
            string assignedPath = assigned != null ? AssetDatabase.GetAssetPath(assigned) : null;
            return !string.IsNullOrEmpty(assignedPath) && assigned.OwnerId == ownerId
                ? assignedPath
                : ResolveStablePath(manager, suffix);
        }

        // The object and prefab-instance ids keep two instances of one prefab apart.
        private static string ResolveStablePath(FlowFieldManager manager, string suffix)
        {
            ValidateFileName(manager.name);
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(manager);
            string scenePath = manager.gameObject.scene.path;
            string sceneGuid = AssetDatabase.AssetPathToGUID(scenePath);
            if (string.IsNullOrEmpty(sceneGuid))
                throw new InvalidOperationException("The scene has no GUID. Save the scene and try again.");
            // Next to the scene, in a folder named after it, as Unity keeps lightmaps.
            string sceneFolder = Path.GetDirectoryName(scenePath)?.Replace('\\', '/');
            string bakeFolder = $"{sceneFolder}/{Path.GetFileNameWithoutExtension(scenePath)}";
            return $"{bakeFolder}/{sceneGuid}_{id.targetObjectId}_{id.targetPrefabId}_{manager.name}_{suffix}.asset";
        }

        /// <summary>
        /// Creates the folders of a project asset path that do not exist yet.
        /// Returns the outermost folder it created, or null when all existed.
        /// </summary>
        internal static string CreateBakeFolder(string assetPath)
        {
            string folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder))
                throw new InvalidOperationException($"Bake target '{assetPath}' has no folder.");
            string[] parts = folder.Split('/');
            string current = parts[0];
            string created = null;
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, parts[i])))
                    {
                        DeleteFolderIfEmpty(created);
                        throw new InvalidOperationException($"Unable to create bake folder '{next}'.");
                    }
                    created ??= next;
                }
                current = next;
            }
            return created;
        }

        // Project paths, not disk paths: a Packages/ path need not exist on disk under the project.
        internal static bool AssetExists(string assetPath)
            => !string.IsNullOrEmpty(assetPath)
                && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets));

        /// <summary>Deletes a folder a failed bake created, unless something else was put in it.</summary>
        internal static void DeleteFolderIfEmpty(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
                return;
            // Every asset below it, subfolders included; only subfolders may remain.
            foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { folder }))
            {
                if (!AssetDatabase.IsValidFolder(AssetDatabase.GUIDToAssetPath(guid)))
                    return;
            }
            AssetDatabase.DeleteAsset(folder);
        }

        /// <summary>
        /// Throws when the target path holds something this Manager must not
        /// overwrite: an object of another type, or a bake another Manager
        /// owns. An unowned bake at this Manager's own stable path is its own.
        /// </summary>
        internal static void ValidateTargetOwnership(
            FlowFieldBakeAsset typedAsset,
            UnityEngine.Object existingAsset,
            string assetPath,
            string modeName,
            string ownerId)
        {
            if (existingAsset != null && typedAsset == null)
                throw new InvalidOperationException(
                    $"{modeName} Bake target '{assetPath}' already exists and is not a {modeName} Bake Asset. "
                    + "Move or delete it before baking.");
            if (typedAsset != null && typedAsset.OwnerId.Length > 0 && typedAsset.OwnerId != ownerId)
                throw new InvalidOperationException(
                    $"{modeName} Bake target '{assetPath}' belongs to another Manager. "
                    + "Move or delete that asset before baking.");
        }

        private static void ValidateFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".."
                || value.IndexOfAny(new[] { '/', '\\' }) >= 0
                || value.IndexOf("..", StringComparison.Ordinal) >= 0)
                throw new ArgumentException("FlowField manager name must be a simple asset file name.", nameof(value));
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalid.Length; i++)
                if (value.IndexOf(invalid[i]) >= 0)
                    throw new ArgumentException("FlowField manager name contains an invalid character.", nameof(value));
        }
    }
}
