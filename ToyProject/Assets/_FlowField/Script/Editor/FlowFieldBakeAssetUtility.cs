using System.IO;
using UnityEditor;

namespace Common.FlowField.Editor
{
    internal static class FlowFieldBakeAssetUtility
    {
        private const string BAKE_DIRECTORY = "Assets/_FlowField/Settings";

        internal static string DeriveSiblingAssetPath(string surfaceAssetPath, string suffix)
        {
            string directory = Path.GetDirectoryName(surfaceAssetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(directory))
                throw new System.ArgumentException("Surface asset path must include an Assets directory.", nameof(surfaceAssetPath));
            string fileName = Path.GetFileNameWithoutExtension(surfaceAssetPath);
            if (fileName.EndsWith("_SurfaceBake"))
                fileName = fileName.Substring(0, fileName.Length - "_SurfaceBake".Length);
            return $"{directory}/{fileName}{suffix}";
        }

        internal static string ResolveAssetPath(FlowFieldManager manager)
        {
            if (manager == null)
                throw new System.ArgumentNullException(nameof(manager));
            if (!manager.gameObject.scene.IsValid() || string.IsNullOrEmpty(manager.gameObject.scene.path))
                throw new System.InvalidOperationException("Scene을 먼저 저장해야 합니다.");

            ValidateFileName(manager.name);
            GlobalObjectId globalId = GlobalObjectId.GetGlobalObjectIdSlow(manager);
            string id = globalId.targetObjectId != 0
                ? globalId.targetObjectId.ToString()
                : manager.GetInstanceID().ToString();
            return $"{BAKE_DIRECTORY}/{manager.name}_{id}_SurfaceBake.asset";
        }

        internal static void CreateBakeFolder()
            => CreateFolderHierarchy(BAKE_DIRECTORY);

        private static void CreateFolderHierarchy(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, parts[i])))
                        throw new System.InvalidOperationException($"Unable to create FlowField bake folder '{next}'.");
                }
                current = next;
            }

            if (!AssetDatabase.IsValidFolder(path))
                throw new System.InvalidOperationException($"FlowField bake folder is not available: {path}");
        }

        private static void ValidateFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value == "."
                || value == ".."
                || value.IndexOfAny(new[] { '/', '\\' }) >= 0
                || value.IndexOf("..", System.StringComparison.Ordinal) >= 0)
                throw new System.ArgumentException("FlowField manager name must be a simple asset file name.", nameof(value));

            char[] invalidCharacters = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalidCharacters.Length; i++)
                if (value.IndexOf(invalidCharacters[i]) >= 0)
                    throw new System.ArgumentException("FlowField manager name contains an invalid character.", nameof(value));
        }
    }
}
