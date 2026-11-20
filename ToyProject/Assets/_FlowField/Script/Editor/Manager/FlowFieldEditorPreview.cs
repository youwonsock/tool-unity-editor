using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// Edit Mode preview of a Surface2D Manager: a short-lived session per
    /// Manager. The first draw solves it at once; after that a change only
    /// marks it stale, the last ready field stays on screen, and the preview
    /// is solved again once the changes have stopped for a moment. A failed
    /// preview keeps its reason until its inputs change. Runtime code never
    /// calls into it; changes are observed here.
    /// </summary>
    [InitializeOnLoad]
    internal static class FlowFieldEditorPreview
    {
        // A drag or a burst of edits rebuilds once, after this quiet time.
        private const double RebuildDelaySeconds = 0.15d;

        private sealed class Entry
        {
            internal FlowFieldSession Session;
            internal string Fault;
            internal bool Stale;
            internal double StaleSince;

            internal void DisposeSession()
            {
                Session?.Dispose();
                Session = null;
            }
        }

        private static readonly Dictionary<FlowFieldManager, Entry> Entries
            = new Dictionary<FlowFieldManager, Entry>();
        private static readonly List<FlowFieldManager> Scratch = new List<FlowFieldManager>();

        static FlowFieldEditorPreview()
        {
            ObjectChangeEvents.changesPublished += OnChangesPublished;
            Undo.undoRedoPerformed += InvalidateAll;
            EditorApplication.playModeStateChanged += _ => DisposeAll();
            EditorApplication.hierarchyChanged += PruneDestroyed;
            EditorApplication.update += RebuildStale;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeAll;
            EditorApplication.quitting += DisposeAll;
        }

        /// <summary>
        /// The preview field. The first request solves it at once; a stale
        /// preview keeps answering with its last ready field until it is solved
        /// again. False while a failed preview waits for its inputs to change.
        /// </summary>
        internal static bool TryGetView(
            FlowFieldManager manager,
            out FlowFieldReadView view,
            out FlowFieldGridSpace grid)
        {
            view = default;
            grid = default;
            if (manager == null)
                return false;
            if (!Entries.TryGetValue(manager, out Entry entry))
            {
                entry = new Entry();
                Entries[manager] = entry;
                Rebuild(manager, entry);
            }
            if (entry.Session == null || !entry.Session.TryGetPublishedField(out FlowFieldField field))
                return false;
            view = FlowFieldReadView.FromSurfaceField(field);
            grid = field.Grid;
            return view.IsValid;
        }

        /// <summary>Why the preview of this Manager failed, until its inputs change.</summary>
        internal static bool TryGetFault(FlowFieldManager manager, out string reason)
        {
            reason = null;
            if (manager == null || !Entries.TryGetValue(manager, out Entry entry))
                return false;
            reason = entry.Fault;
            return !string.IsNullOrEmpty(reason);
        }

        /// <summary>The Manager's inputs changed: solve its preview again once they settle.</summary>
        internal static void Invalidate(FlowFieldManager manager)
        {
            if (manager is null || !Entries.TryGetValue(manager, out Entry entry))
                return;
            entry.Stale = true;
            entry.StaleSince = EditorApplication.timeSinceStartup;
        }

        internal static void InvalidateAll()
        {
            double now = EditorApplication.timeSinceStartup;
            foreach (Entry entry in Entries.Values)
            {
                entry.Stale = true;
                entry.StaleSince = now;
            }
        }

        // Play Mode, a domain reload and quitting end every preview at once.
        private static void DisposeAll()
        {
            foreach (Entry entry in Entries.Values)
                entry.DisposeSession();
            Entries.Clear();
        }

        private static void RebuildStale()
        {
            if (Entries.Count == 0 || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            double now = EditorApplication.timeSinceStartup;
            Scratch.Clear();
            foreach (KeyValuePair<FlowFieldManager, Entry> pair in Entries)
            {
                if (pair.Value.Stale && now - pair.Value.StaleSince >= RebuildDelaySeconds)
                    Scratch.Add(pair.Key);
            }
            if (Scratch.Count == 0)
                return;
            foreach (FlowFieldManager manager in Scratch)
            {
                if (manager == null)
                {
                    Entries[manager].DisposeSession();
                    Entries.Remove(manager);
                    continue;
                }
                Rebuild(manager, Entries[manager]);
            }
            SceneView.RepaintAll();
        }

        private static void Rebuild(FlowFieldManager manager, Entry entry)
        {
            entry.Stale = false;
            entry.DisposeSession();
            entry.Fault = null;
            FlowFieldSession session = FlowFieldSession.CreateSurface();
            try
            {
                session.Initialize(manager.BakeMode);
                RegisterModifiers(manager, session);
                // The same submission as a running Manager, without the runtime scheduler.
                session.SubmitConfig(manager.CaptureConfig(), FlowFieldConfigChange.Rebuild);
                // The preview owns its session and waits for the worker, so it
                // completes in the same editor update that asked for it.
                session.Pump();
            }
            catch (Exception exception)
            {
                session.Dispose();
                entry.Fault = exception.Message;
                return;
            }
            if (session.IsFaulted)
            {
                entry.Fault = session.LastError;
                session.Dispose();
                return;
            }
            entry.Session = session;
        }

        // Only the Modifiers a running Manager would register: enabled ones in
        // a scene, for this Manager, in Priority order.
        private static void RegisterModifiers(FlowFieldManager manager, FlowFieldSession session)
        {
            FlowFieldVectorModifierVolume[] volumes = UnityEngine.Object.FindObjectsByType<FlowFieldVectorModifierVolume>(
                FindObjectsInactive.Exclude,
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
                    || !volume.isActiveAndEnabled
                    || volume.FlowFieldManager != manager
                    || !volume.gameObject.scene.IsValid())
                    continue;
                // A rejected Modifier (a duplicate Priority, say) stays out, as
                // it would at runtime; the rebuild waits for the next change.
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

        // An edited Manager or Modifier marks its preview stale. Any structural
        // change can add or remove a Modifier, so it marks every preview.
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
            Scratch.Clear();
            foreach (FlowFieldManager manager in Entries.Keys)
            {
                if (manager == null || !manager.isActiveAndEnabled)
                    Scratch.Add(manager);
            }
            foreach (FlowFieldManager manager in Scratch)
            {
                Entries[manager].DisposeSession();
                Entries.Remove(manager);
            }
            FlowFieldVolumeVisualizationEditor.PruneInactive();
        }
    }
}
