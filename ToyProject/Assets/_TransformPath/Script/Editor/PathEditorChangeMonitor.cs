#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Common.TransformPath
{
    /// <summary>Shared edit-mode polling and invalidation subscriptions for path linkers.</summary>
    [InitializeOnLoad]
    internal static class PathEditorChangeMonitor
    {
        private const double SYNC_INTERVAL = 0.05d;
        private static double _lastSyncTime;
        public static event Action Updated;
        public static event Action StructureChanged;
        public static event Action<UnityEngine.Object> DefinitionChanged;
        public static event Action UndoRedo;
        public static event Action BeforeReload;

        static PathEditorChangeMonitor()
        {
            EditorApplication.update += Update;
            EditorApplication.hierarchyChanged += MarkStructureChanged;
            EditorSceneManager.sceneOpened += SceneOpened;
            EditorSceneManager.sceneClosed += SceneClosed;
            PrefabStage.prefabStageOpened += PrefabChanged;
            PrefabStage.prefabStageClosing += PrefabChanged;
            Undo.undoRedoPerformed += HandleUndoRedo;
            ObjectChangeEvents.changesPublished += HandleObjectChanges;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }

        private static void Update()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling
                || EditorApplication.timeSinceStartup - _lastSyncTime < SYNC_INTERVAL)
                return;
            _lastSyncTime = EditorApplication.timeSinceStartup;
            Updated?.Invoke();
        }

        private static void HandleObjectChanges(ref ObjectChangeEventStream stream)
        {
            for (int i = 0; i < stream.length; i++)
            {
                ObjectChangeKind kind = stream.GetEventType(i);
                if (kind == ObjectChangeKind.ChangeScene
                    || kind == ObjectChangeKind.CreateGameObjectHierarchy
                    || kind == ObjectChangeKind.DestroyGameObjectHierarchy
                    || kind == ObjectChangeKind.ChangeGameObjectStructure
                    || kind == ObjectChangeKind.ChangeGameObjectStructureHierarchy
                    || kind == ObjectChangeKind.ChangeGameObjectParent
                    || kind == ObjectChangeKind.ChangeChildrenOrder)
                {
                    MarkStructureChanged();
                    return;
                }
                if (kind != ObjectChangeKind.ChangeGameObjectOrComponentProperties) continue;
                stream.GetChangeGameObjectOrComponentPropertiesEvent(i,
                    out ChangeGameObjectOrComponentPropertiesEventArgs change);
                UnityEngine.Object target = EditorUtility.InstanceIDToObject(change.instanceId);
                // Position changes must retain the previous movement snapshots.
                if (target is PathData || target is MultiPathData || target is PathWaypointSyncRegistry)
                    DefinitionChanged?.Invoke(target);
            }
        }

        private static void MarkStructureChanged() => StructureChanged?.Invoke();
        private static void SceneOpened(Scene scene, OpenSceneMode mode) => MarkStructureChanged();
        private static void SceneClosed(Scene scene) => MarkStructureChanged();
        private static void PrefabChanged(PrefabStage stage) => MarkStructureChanged();
        private static void HandleUndoRedo() => UndoRedo?.Invoke();

        private static void Clear()
        {
            BeforeReload?.Invoke();
            EditorApplication.update -= Update;
            EditorApplication.hierarchyChanged -= MarkStructureChanged;
            EditorSceneManager.sceneOpened -= SceneOpened;
            EditorSceneManager.sceneClosed -= SceneClosed;
            PrefabStage.prefabStageOpened -= PrefabChanged;
            PrefabStage.prefabStageClosing -= PrefabChanged;
            Undo.undoRedoPerformed -= HandleUndoRedo;
            ObjectChangeEvents.changesPublished -= HandleObjectChanges;
            AssemblyReloadEvents.beforeAssemblyReload -= Clear;
            Updated = null;
            StructureChanged = null;
            DefinitionChanged = null;
            UndoRedo = null;
            BeforeReload = null;
        }
    }
}
#endif
