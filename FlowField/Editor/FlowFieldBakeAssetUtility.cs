using System.IO;
using UnityEditor;

namespace Supercent.Common.FlowField.Editor
{
    internal static class FlowFieldBakeAssetUtility
    {
        internal static string DeriveSiblingAssetPath(string surfaceAssetPath, string suffix)
        {
            string directory = Path.GetDirectoryName(surfaceAssetPath)?.Replace('\\', '/') ?? "Assets";
            string fileName = Path.GetFileNameWithoutExtension(surfaceAssetPath);
            if (fileName.EndsWith("_SurfaceBake"))
                fileName = fileName.Substring(0, fileName.Length - "_SurfaceBake".Length);
            return $"{directory}/{fileName}{suffix}";
        }

        internal static bool TryResolveAssetPath(
            FlowFieldManager manager,
            out string assetPath,
            out string error)
        {
            assetPath = string.Empty;
            error = string.Empty;
            if (!manager.gameObject.scene.IsValid() || string.IsNullOrEmpty(manager.gameObject.scene.path))
            {
                error = "Scene을 먼저 저장해야 합니다.";
                return false;
            }

            string sceneDirectory = Path.GetDirectoryName(manager.gameObject.scene.path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(sceneDirectory))
            {
                error = "Scene 폴더를 확인할 수 없습니다.";
                return false;
            }

            string bakeDirectory = sceneDirectory + "/FlowFieldBakes";
            EnsureFolder(bakeDirectory);
            GlobalObjectId globalId = GlobalObjectId.GetGlobalObjectIdSlow(manager);
            string id = globalId.targetObjectId != 0
                ? globalId.targetObjectId.ToString()
                : manager.GetInstanceID().ToString();
            assetPath = $"{bakeDirectory}/{SanitizeFileName(manager.name)}_{id}_SurfaceBake.asset";
            return true;
        }

        private static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string SanitizeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return value.Replace('/', '_');
        }
    }
}
