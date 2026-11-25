using UnityEditor;

namespace Common.FlowField.Editor
{
    /// <summary>Drops the Volume3D display's bake validation when an asset file is imported, deleted or moved.</summary>
    internal sealed class FlowFieldVolumeVisualizationAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            Invalidate(importedAssets);
            Invalidate(deletedAssets);
            Invalidate(movedAssets);
            Invalidate(movedFromAssetPaths);
        }

        private static void Invalidate(string[] paths)
        {
            if (paths == null)
                return;
            for (int i = 0; i < paths.Length; i++)
                if (!string.IsNullOrEmpty(paths[i])
                    && paths[i].EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase))
                    FlowFieldVolumeVisualizationEditor.InvalidateAssetPath(paths[i]);
        }
    }
}
