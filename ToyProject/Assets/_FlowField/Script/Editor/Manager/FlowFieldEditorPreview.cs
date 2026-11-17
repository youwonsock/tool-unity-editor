using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// Edit Mode preview of a Surface2D Manager: a short-lived session per
    /// Manager, solved on demand and dropped whenever its inputs may have
    /// changed. Runtime code never calls into it; changes are observed here.
    /// </summary>
    [InitializeOnLoad]
    internal static class FlowFieldEditorPreview
    {
        private static readonly Dictionary<FlowFieldManager, FlowFieldSession> Sessions
            = new Dictionary<FlowFieldManager, FlowFieldSession>();
        private static readonly List<FlowFieldManager> Stale = new List<FlowFieldManager>();

        static FlowFieldEditorPreview()
        {
            ObjectChangeEvents.changesPublished += OnChangesPublished;
            Undo.undoRedoPerformed += InvalidateAll;
            EditorApplication.playModeStateChanged += _ => InvalidateAll();
            EditorApplication.hierarchyChanged += PruneDestroyed;
            AssemblyReloadEvents.beforeAssemblyReload += InvalidateAll;
            EditorApplication.quitting += InvalidateAll;
        }

        /// <summary>Returns the preview field, solving it first when none is current.</summary>
        internal static bool TryGetView(
            FlowFieldManager manager,
            out FlowFieldReadView view,
            out FlowFieldGridSpace grid)
        {
            view = default;
            grid = default;
            if (manager == null)
                return false;
            if (!Sessions.TryGetValue(manager, out FlowFieldSession session) || !session.IsReady)
            {
                Invalidate(manager);
                session = Build(manager);
                if (session == null)
                    return false;
                Sessions[manager] = session;
            }
            if (!session.TryGetPublishedField(out FlowFieldField field))
                return false;
            view = FlowFieldReadView.FromSurfaceField(field);
            grid = field.Grid;
            return view.IsValid;
        }

        internal static void Invalidate(FlowFieldManager manager)
        {
            if (manager is null || !Sessions.TryGetValue(manager, out FlowFieldSession session))
                return;
            Sessions.Remove(manager);
            session.Dispose();
        }

        internal static void InvalidateAll()
        {
            foreach (FlowFieldSession session in Sessions.Values)
                session.Dispose();
            Sessions.Clear();
        }

        private static FlowFieldSession Build(FlowFieldManager manager)
        {
            var session = FlowFieldSession.CreateSurface();
            session.Initialize(manager.BakeMode);
            RegisterModifiers(manager, session);
            // The same submission as a running Manager, without the runtime scheduler.
            session.SubmitConfig(manager.CaptureConfig(), FlowFieldConfigChange.Rebuild);
            // The preview owns its session and waits for the worker, so it
            // completes in the same editor repaint that asked for it.
            session.Pump();
            return session;
        }

        private static void RegisterModifiers(FlowFieldManager manager, FlowFieldSession session)
        {
            FlowFieldVectorModifierVolume[] volumes = UnityEngine.Object.FindObjectsByType<FlowFieldVectorModifierVolume>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            Array.Sort(volumes, (left, right) =>
            {
                int priority = left.Priority.CompareTo(right.Priority);
                return priority != 0
                    ? priority
                    : left.GetInstanceID().CompareTo(right.GetInstanceID());
            });
            for (int i = 0; i < volumes.Length; i++)
            {
                FlowFieldVectorModifierVolume volume = volumes[i];
                if (volume == null
                    || volume.FlowFieldManager != manager
                    || !volume.gameObject.scene.IsValid())
                    continue;
                try
                {
                    session.RegisterModifierCommand(volume);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, volume);
                }
            }
        }

        // An edited Manager or Modifier drops its preview. Any structural
        // change can add or remove a Modifier, so it drops every preview.
        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            for (int i = 0; i < stream.length; i++)
            {
                if (stream.GetEventType(i) != ObjectChangeKind.ChangeGameObjectOrComponentProperties)
                {
                    InvalidateAll();
                    FlowFieldVolumeVisualizationEditor.InvalidateAll();
                    continue;
                }
                stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var change);
                UnityEngine.Object changed = EditorUtility.InstanceIDToObject(change.instanceId);
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
                    // surface or an obstacle of any Manager.
                    if (changed is Component || changed is GameObject)
                        InvalidateAll();
                    continue;
                }
                Invalidate(manager);
                FlowFieldVolumeVisualizationEditor.RequestRefresh(manager);
            }
        }

        private static void PruneDestroyed()
        {
            Stale.Clear();
            foreach (FlowFieldManager manager in Sessions.Keys)
            {
                if (manager == null || !manager.isActiveAndEnabled)
                    Stale.Add(manager);
            }
            foreach (FlowFieldManager manager in Stale)
                Invalidate(manager);
            FlowFieldVolumeVisualizationEditor.PruneInactive();
        }
    }
}
