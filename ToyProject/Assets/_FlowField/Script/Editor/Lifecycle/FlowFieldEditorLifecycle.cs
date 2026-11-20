using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// The one place the FlowField Editor subscribes to editor events. It
    /// tells both displays what changed: the Surface2D Edit Mode preview
    /// (<see cref="FlowFieldEditorPreview"/>) and the Volume3D display
    /// (<see cref="FlowFieldVolumeVisualizationEditor"/>). Managers join a
    /// display when the Scene View first draws them and leave it when they
    /// are disabled or destroyed. Every subscription is dropped before an
    /// assembly reload and on quitting, so disabling domain reload cannot
    /// accumulate callbacks.
    /// </summary>
    [InitializeOnLoad]
    internal static class FlowFieldEditorLifecycle
    {
        private static bool _registered;

        static FlowFieldEditorLifecycle()
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
            ObjectChangeEvents.changesPublished += OnChangesPublished;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorApplication.quitting += Unregister;
            AssemblyReloadEvents.beforeAssemblyReload += Unregister;
        }

        private static void OnEditorUpdate()
        {
            // The preview rebuilds once its changes have settled.
            FlowFieldEditorPreview.RebuildStale();
            FlowFieldVolumeVisualizationEditor.PumpAll(2.0);
        }

        private static void OnUndoRedo()
        {
            FlowFieldModifierPriorities.ClearCache();
            FlowFieldEditorPreview.InvalidateAll();
            FlowFieldVolumeVisualizationEditor.InvalidateAll();
        }

        private static void OnHierarchyChanged()
        {
            FlowFieldModifierPriorities.ClearCache();
            FlowFieldEditorPreview.PruneDestroyed();
            FlowFieldVolumeVisualizationEditor.PruneDestroyed();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Every Play Mode change ends the Edit Mode previews.
            FlowFieldModifierPriorities.ClearCache();
            FlowFieldEditorPreview.DisposeAll();
            if (change == PlayModeStateChange.ExitingEditMode
                || change == PlayModeStateChange.EnteredEditMode
                || change == PlayModeStateChange.EnteredPlayMode)
                FlowFieldVolumeVisualizationEditor.InvalidateAll();
        }

        private static void OnSceneClosing(Scene scene, bool removingScene)
        {
            FlowFieldModifierPriorities.ClearCache();
            FlowFieldVolumeVisualizationEditor.InvalidateAll();
        }

        // An edited Manager or Modifier marks its own displays stale. Any
        // structural change can add or remove a Modifier, so it marks every
        // display.
        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            // Any change can add, remove or re-prioritize a Modifier Volume.
            if (stream.length > 0)
                FlowFieldModifierPriorities.ClearCache();
            for (int i = 0; i < stream.length; i++)
            {
                if (stream.GetEventType(i) != ObjectChangeKind.ChangeGameObjectOrComponentProperties)
                {
                    FlowFieldEditorPreview.InvalidateAll();
                    FlowFieldVolumeVisualizationEditor.InvalidateAll();
                    continue;
                }
                stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var change);
                Object changed = EditorUtility.InstanceIDToObject(change.instanceId);
                FlowFieldManager manager = changed switch
                {
                    FlowFieldManager m => m,
                    FlowFieldVectorModifierVolume volume => volume.FlowFieldManager,
                    FlowFieldDisplaySettings display => display.GetComponent<FlowFieldManager>(),
                    Component component => component.GetComponent<FlowFieldManager>(),
                    GameObject gameObject => gameObject.GetComponent<FlowFieldManager>(),
                    _ => null,
                };
                if (manager == null)
                {
                    // A moved Transform or edited Collider can change the
                    // surface or an obstacle of any Surface2D preview.
                    if (changed is Component || changed is GameObject)
                        FlowFieldEditorPreview.InvalidateAll();
                    continue;
                }
                FlowFieldEditorPreview.Invalidate(manager);
                FlowFieldVolumeVisualizationEditor.RequestRefresh(manager);
            }
        }

        private static void Unregister()
        {
            if (!_registered)
                return;
            EditorApplication.update -= OnEditorUpdate;
            Undo.undoRedoPerformed -= OnUndoRedo;
            ObjectChangeEvents.changesPublished -= OnChangesPublished;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorSceneManager.sceneClosing -= OnSceneClosing;
            EditorApplication.quitting -= Unregister;
            AssemblyReloadEvents.beforeAssemblyReload -= Unregister;
            _registered = false;
            FlowFieldModifierPriorities.ClearCache();
            FlowFieldEditorPreview.DisposeAll();
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
