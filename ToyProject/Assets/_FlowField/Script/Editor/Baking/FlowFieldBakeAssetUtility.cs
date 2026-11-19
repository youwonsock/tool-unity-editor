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
        /// Where a bake writes: the assigned asset, or else this Manager's
        /// stable path next to its scene.
        /// </summary>
        internal static string ResolveAssetPath(FlowFieldManager manager, UnityEngine.Object assigned, string suffix)
        {
            if (!manager.gameObject.scene.IsValid() || string.IsNullOrEmpty(manager.gameObject.scene.path))
                throw new InvalidOperationException("Scene을 먼저 저장해야 합니다.");
            string assignedPath = assigned != null ? AssetDatabase.GetAssetPath(assigned) : null;
            return string.IsNullOrEmpty(assignedPath) ? ResolveStablePath(manager, suffix) : assignedPath;
        }

        private static string ResolveStablePath(FlowFieldManager manager, string suffix)
        {
            ValidateFileName(manager.name);
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(manager);
            string managerId = id.targetObjectId != 0
                ? id.targetObjectId.ToString()
                : manager.GetInstanceID().ToString();
            string scenePath = manager.gameObject.scene.path;
            string sceneGuid = AssetDatabase.AssetPathToGUID(scenePath);
            if (string.IsNullOrEmpty(sceneGuid))
                throw new InvalidOperationException("씬 GUID를 확인할 수 없습니다. 씬을 저장한 뒤 다시 시도하세요.");
            // Next to the scene, in a folder named after it, as Unity keeps lightmaps.
            string sceneFolder = Path.GetDirectoryName(scenePath)?.Replace('\\', '/');
            string bakeFolder = $"{sceneFolder}/{Path.GetFileNameWithoutExtension(scenePath)}";
            return $"{bakeFolder}/{sceneGuid}_{managerId}_{manager.name}_{suffix}.asset";
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

        internal static void ValidateTargetOwnership(
            UnityEngine.Object assignedAsset,
            UnityEngine.Object typedAsset,
            UnityEngine.Object existingAsset,
            string assetPath,
            string modeName)
        {
            if (existingAsset != null && typedAsset == null)
                throw new InvalidOperationException(
                    $"{modeName} Bake target '{assetPath}' already exists and is not assigned to this Manager. "
                    + "Choose a new asset or explicitly assign the existing asset before baking.");
            // With no reference, the path is this Manager's own, as after Clear Bake.
            if (typedAsset != null && assignedAsset != null && assignedAsset != typedAsset)
                throw new InvalidOperationException(
                    $"{modeName} Bake target '{assetPath}' is shared by another reference. "
                    + "Create a separate Bake Asset before overwriting it.");
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
