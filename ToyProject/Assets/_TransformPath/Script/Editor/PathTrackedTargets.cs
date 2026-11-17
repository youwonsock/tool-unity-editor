using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.TransformPath
{
    /// <summary>One scene object an Editor linker keeps in sync.</summary>
    internal interface IPathTrackedTarget
    {
        int Id { get; }
        bool IsAlive { get; }
        /// <summary>Reads the target's references again after a structural change.</summary>
        void RefreshReferences();
        /// <summary>Applies one sync pass. Returns whether it moved anything.</summary>
        bool Sync();
    }

    /// <summary>
    /// The scene objects of one type an Editor linker follows. It finds them
    /// again only after a structural change and syncs each one every Editor update.
    /// </summary>
    internal sealed class PathTrackedTargets<TTarget, TTracked>
        where TTarget : Object
        where TTracked : class, IPathTrackedTarget
    {
        private readonly List<TTracked> _tracked = new List<TTracked>();
        private readonly Dictionary<int, TTracked> _byId = new Dictionary<int, TTracked>();
        private readonly HashSet<int> _liveIds = new HashSet<int>();
        private readonly Func<TTarget, TTracked> _create;
        private readonly Func<Object, bool> _isDefinition;
        private readonly Action<PathTrackedTargets<TTarget, TTracked>> _onUndoRedo;
        private bool _dirty = true;

        public PathTrackedTargets(
            Func<TTarget, TTracked> create,
            Func<Object, bool> isDefinition,
            Action<PathTrackedTargets<TTarget, TTracked>> onUndoRedo = null)
        {
            _create = create;
            _isDefinition = isDefinition;
            _onUndoRedo = onUndoRedo;
            PathEditorChangeMonitor.Updated += Update;
            PathEditorChangeMonitor.StructureChanged += MarkDirty;
            PathEditorChangeMonitor.DefinitionChanged += HandleDefinitionChanged;
            PathEditorChangeMonitor.UndoRedo += HandleUndoRedo;
            PathEditorChangeMonitor.BeforeReload += Clear;
        }

        public IReadOnlyList<TTracked> Tracked => _tracked;

        public void MarkDirty() => _dirty = true;

        /// <summary>Optional cleanup when the domain reloads, after the list is cleared.</summary>
        public event Action Cleared;

        private void Update()
        {
            if (_dirty)
                Refresh();

            bool syncedAny = false;
            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                TTracked tracked = _tracked[i];
                if (!tracked.IsAlive)
                {
                    _byId.Remove(tracked.Id);
                    _tracked.RemoveAt(i);
                    continue;
                }
                if (tracked.Sync())
                    syncedAny = true;
            }
            if (syncedAny)
                PathEditorUndoUtility.RepaintScene();
        }

        private void Refresh()
        {
            TTarget[] targets = Object.FindObjectsByType<TTarget>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            _liveIds.Clear();
            for (int i = 0; i < targets.Length; i++)
            {
                TTarget target = targets[i];
                if (target == null)
                    continue;
                int id = target.GetInstanceID();
                _liveIds.Add(id);
                if (!_byId.TryGetValue(id, out TTracked tracked))
                {
                    tracked = _create(target);
                    _byId.Add(id, tracked);
                    _tracked.Add(tracked);
                }
                tracked.RefreshReferences();
            }
            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                if (_liveIds.Contains(_tracked[i].Id))
                    continue;
                _byId.Remove(_tracked[i].Id);
                _tracked.RemoveAt(i);
            }
            _dirty = false;
        }

        private void HandleDefinitionChanged(Object changedObject)
        {
            if (_isDefinition(changedObject))
                MarkDirty();
        }

        private void HandleUndoRedo()
        {
            if (_onUndoRedo == null)
                MarkDirty();
            else
                _onUndoRedo(this);
        }

        private void Clear()
        {
            PathEditorChangeMonitor.Updated -= Update;
            PathEditorChangeMonitor.StructureChanged -= MarkDirty;
            PathEditorChangeMonitor.DefinitionChanged -= HandleDefinitionChanged;
            PathEditorChangeMonitor.UndoRedo -= HandleUndoRedo;
            PathEditorChangeMonitor.BeforeReload -= Clear;
            _tracked.Clear();
            _byId.Clear();
            _liveIds.Clear();
            Cleared?.Invoke();
        }
    }
}
