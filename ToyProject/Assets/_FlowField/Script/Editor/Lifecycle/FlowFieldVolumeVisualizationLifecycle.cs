#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// Keeps the display service alive only through editor events. Managers
    /// join it when the Scene View first draws them and leave it when they are
    /// disabled or destroyed; disabling domain reload therefore cannot
    /// accumulate update or undo callbacks.
    /// </summary>
    [InitializeOnLoad]
    internal static class FlowFieldVolumeVisualizationLifecycle
    {
        private static bool _registered;

        static FlowFieldVolumeVisualizationLifecycle()
        {
            Register();
        }

        private static void Register()
        {
            if (_registered)
                return;
            _registered = true;
            EditorApplication.update += OnEditorUpdate;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorApplication.quitting += OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void OnEditorUpdate()
            => FlowFieldVolumeVisualizationEditor.PumpAll(2.0);

        private static void OnUndoRedo()
            => FlowFieldVolumeVisualizationEditor.InvalidateAll();

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode
                || change == PlayModeStateChange.EnteredEditMode
                || change == PlayModeStateChange.EnteredPlayMode)
                FlowFieldVolumeVisualizationEditor.InvalidateAll();
        }

        private static void OnSceneClosing(Scene scene, bool removingScene)
            => FlowFieldVolumeVisualizationEditor.InvalidateAll();

        private static void OnEditorQuitting()
            => Unregister(true);

        private static void OnBeforeAssemblyReload()
            => Unregister(true);

        private static void Unregister(bool clearState)
        {
            if (!_registered)
                return;
            EditorApplication.update -= OnEditorUpdate;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorSceneManager.sceneClosing -= OnSceneClosing;
            EditorApplication.quitting -= OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            _registered = false;
            if (clearState)
                FlowFieldVolumeVisualizationEditor.Clear();
        }
    }

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
#endif
