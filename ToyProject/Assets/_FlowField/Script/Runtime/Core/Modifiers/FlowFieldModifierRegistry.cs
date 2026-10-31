using System;
using System.Buffers;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal sealed class FlowFieldModifierRegistry
    {
        internal sealed class Entry
        {
            internal readonly IFlowFieldVectorModifier Modifier;
            internal Collider InfluenceCollider;
            internal int Priority;
            internal int Revision;
            internal bool AreaDirty = true;
            // Surface-only buffers remain lazy until the first area rebuild.
            internal bool[] InfluenceMask;
            internal bool[] InfluenceScratch;
            internal List<int> InfluenceIndices;
            internal bool InfluenceIndicesBuilt;
            internal FlowFieldCellRect InfluenceRect = FlowFieldCellRect.Invalid;
            internal bool SnapshotInitialized;
            internal bool LastEnabled;
            internal bool LastActive;
            internal bool LastTrigger;
            internal Vector3 LastPosition;
            internal Quaternion LastRotation;
            internal Vector3 LastScale;
            internal Bounds LastBounds;
            internal void ReleaseArea()
            {
                InfluenceMask = null;
                InfluenceScratch = null;
                InfluenceIndices?.Clear();
                InfluenceIndicesBuilt = false;
                InfluenceRect = FlowFieldCellRect.Invalid;
            }

            internal Entry(
                IFlowFieldVectorModifier modifier,
                Collider influenceCollider,
                int priority,
                int revision)
            {
                Modifier = modifier;
                InfluenceCollider = influenceCollider;
                Priority = priority;
                Revision = revision;
            }
        }

        private readonly List<Entry> _entries = new List<Entry>(16);
        private int _operationVersion;

        internal IReadOnlyList<Entry> Entries => _entries;
        internal int OperationVersion => _operationVersion;
        internal bool IsComposing { get; private set; }

        internal FlowFieldModifierRegistryResult Register(IFlowFieldVectorModifier modifier)
        {
            if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                throw new ArgumentNullException(nameof(modifier));
            int version = BeginOperation();
            return RegisterImmediately(modifier, version);
        }

        internal FlowFieldModifierRegistryResult Unregister(IFlowFieldVectorModifier modifier)
        {
            if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                throw new ArgumentNullException(nameof(modifier));
            BeginOperation();

            return UnregisterImmediately(modifier);
        }

        internal FlowFieldModifierRegistryResult MarkDirty(
            IFlowFieldVectorModifier modifier,
            bool valueNotified,
            out bool completed)
        {
            completed = false;
            if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                throw new ArgumentNullException(nameof(modifier));
            int version = BeginOperation();
            Entry entry = Find(modifier);
            if (entry == null)
                throw new InvalidOperationException("Modifier is not registered.");

            bool finalDirty = false;
            bool valueDirty = false;
            bool priorityChanged = false;
            if (!TryReadMetadata(modifier, version, out Collider collider, out int priority, out int revision))
                return default;
            ValidateConfiguration(collider);
            if (entry.Priority != priority && HasPriorityConflict(modifier, priority))
                throw new InvalidOperationException($"Vector modifier priority {priority} is duplicated.");

            entry.InfluenceCollider = collider;
            if (entry.Priority != priority)
            {
                entry.Priority = priority;
                _entries.Sort(CompareEntries);
                priorityChanged = true;
                finalDirty = true;
            }
            if (valueNotified || entry.Revision != revision)
            {
                entry.Revision = revision;
                valueDirty = true;
                finalDirty = true;
            }

            FlowFieldCellRect dirty = entry.InfluenceRect;
            if (priorityChanged)
                dirty = UnionAllInfluenceRects();
            completed = true;
            return new FlowFieldModifierRegistryResult(false, valueDirty, finalDirty, dirty);
        }

        internal FlowFieldModifierRegistryResult MarkAreaDirty(IFlowFieldVectorModifier modifier)
        {
            if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                throw new ArgumentNullException(nameof(modifier));
            BeginOperation();
            Entry entry = Find(modifier);
            if (entry == null)
                throw new InvalidOperationException("Modifier is not registered.");

            entry.AreaDirty = true;
            return new FlowFieldModifierRegistryResult(
                true,
                false,
                false,
                entry.InfluenceRect);
        }

        internal FlowFieldModifierRegistryResult DetectChanges()
        {
            int version = BeginOperation();
            // Keep tentative observations local: nested observation owns its own
            // values and no cache is changed until every getter has completed.
            if (_entries.Count == 0)
                return default;
            var observations = ArrayPool<(Collider collider, int priority, int revision)>.Shared.Rent(_entries.Count);
            try
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    IFlowFieldVectorModifier modifier = _entries[i].Modifier;
                    if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                        continue;
                    if (!TryReadMetadata(modifier, version, out Collider collider, out int priority, out int revision))
                        return default;
                    ValidateConfiguration(collider);
                    for (int previous = 0; previous < i; previous++)
                        if (!FlowFieldVectorUtility.IsMissingModifier(_entries[previous].Modifier) && observations[previous].priority == priority)
                            throw new InvalidOperationException($"Vector modifier priority {priority} is duplicated.");
                    observations[i] = (collider, priority, revision);
                }
                bool areaDirty = false;
                bool valueDirty = false;
                bool finalDirty = false;
                bool priorityChanged = false;
                FlowFieldCellRect dirtyRegion = FlowFieldCellRect.Invalid;
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    Entry entry = _entries[i];
                    IFlowFieldVectorModifier modifier = entry.Modifier;
                    if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                    {
                        dirtyRegion = FlowFieldCellRect.Union(
                            dirtyRegion,
                            entry.InfluenceRect);
                        entry.ReleaseArea();
                        _entries.RemoveAt(i);
                        finalDirty = true;
                        continue;
                    }

                    var (collider, priority, revision) = observations[i];

                    if (entry.Priority != priority)
                    {
                        entry.Priority = priority;
                        priorityChanged = true;
                        finalDirty = true;
                    }

                    if (entry.Revision != revision)
                    {
                        entry.Revision = revision;
                        valueDirty = true;
                        finalDirty = true;
                    }

                    if (entry.AreaDirty || HasColliderSnapshotChanged(entry, collider))
                    {
                        dirtyRegion = FlowFieldCellRect.Union(
                            dirtyRegion,
                            entry.InfluenceRect);
                        entry.InfluenceCollider = collider;
                        entry.AreaDirty = true;
                        areaDirty = true;
                        // Treat this observation as the latest input even before
                        // the next composition runs. Otherwise an unchanged
                        // collider would be reported dirty on every poll while a
                        // GPU solve is in flight. The area flag remains set, so
                        // the subsequent build still recomputes its mask.
                        UpdateColliderSnapshot(entry, collider);
                        if (collider != null)
                            dirtyRegion = FlowFieldCellRect.Union(
                                dirtyRegion,
                                entry.InfluenceRect);
                    }

                }

                if (priorityChanged)
                {
                    _entries.Sort(CompareEntries);
                    dirtyRegion = UnionAllInfluenceRects();
                }

                return new FlowFieldModifierRegistryResult(areaDirty, valueDirty, finalDirty, dirtyRegion);
            }
            finally
            {
                ArrayPool<(Collider, int, int)>.Shared.Return(observations, clearArray: true);
            }
        }

        internal void BeginComposition()
            => IsComposing = true;

        internal void EndComposition()
            => IsComposing = false;

        internal void MarkAllAreasDirty()
        {
            for (int i = 0; i < _entries.Count; i++)
                _entries[i].AreaDirty = true;
        }

        internal void UpdateColliderSnapshot(Entry entry, Collider collider)
        {
            entry.SnapshotInitialized = true;
            if (collider == null)
            {
                entry.LastEnabled = false;
                entry.LastActive = false;
                entry.LastTrigger = false;
                entry.LastPosition = default;
                entry.LastRotation = Quaternion.identity;
                entry.LastScale = default;
                entry.LastBounds = default;
                return;
            }

            Transform colliderTransform = collider.transform;
            entry.LastEnabled = collider.enabled;
            entry.LastActive = collider.gameObject.activeInHierarchy;
            entry.LastTrigger = collider.isTrigger;
            entry.LastPosition = colliderTransform.position;
            entry.LastRotation = colliderTransform.rotation;
            entry.LastScale = colliderTransform.lossyScale;
            entry.LastBounds = collider.bounds;
        }

        internal void Clear()
        {
            InvalidateOperations();
            for (int i = 0; i < _entries.Count; i++)
                _entries[i].ReleaseArea();
            _entries.Clear();
            IsComposing = false;
        }

        private FlowFieldModifierRegistryResult RegisterImmediately(IFlowFieldVectorModifier modifier, int version)
        {
            if (Find(modifier) != null)
                throw new InvalidOperationException("Modifier is already registered.");

            if (!TryReadMetadata(modifier, version, out Collider collider, out int priority, out int revision))
                return default;

            ValidateConfiguration(collider);
            if (HasPriorityConflict(modifier, priority))
                throw new InvalidOperationException($"Vector modifier priority {priority} is duplicated.");

            Entry entry = new Entry(modifier, collider, priority, revision);
            UpdateColliderSnapshot(entry, collider);
            _entries.Add(entry);
            _entries.Sort(CompareEntries);
            return new FlowFieldModifierRegistryResult(true, false, true, FlowFieldCellRect.Invalid);
        }

        private FlowFieldModifierRegistryResult UnregisterImmediately(IFlowFieldVectorModifier modifier)
        {
            bool removed = false;
            FlowFieldCellRect dirty = FlowFieldCellRect.Invalid;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_entries[i].Modifier, modifier))
                    continue;

                dirty = FlowFieldCellRect.Union(
                    dirty,
                    _entries[i].InfluenceRect);
                _entries[i].ReleaseArea();
                _entries.RemoveAt(i);
                removed = true;
            }

            if (!removed)
                throw new InvalidOperationException("Modifier is not registered.");
            return new FlowFieldModifierRegistryResult(false, false, true, dirty);
        }

        private void ValidateConfiguration(Collider collider)
        {
            if (collider == null)
                throw new InvalidOperationException("Influence Collider is required.");

            if (!collider.isTrigger)
            {
                throw new ArgumentException(FlowFieldModifierMaskBuilder.TriggerRequiredMessage, nameof(collider));
            }

            if (collider is MeshCollider meshCollider && !meshCollider.convex)
            {
                throw new ArgumentException(FlowFieldModifierMaskBuilder.ConvexMeshRequiredMessage, nameof(collider));
            }

        }

        private bool HasPriorityConflict(IFlowFieldVectorModifier ignoredModifier, int priority)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry entry = _entries[i];
                if (!ReferenceEquals(entry.Modifier, ignoredModifier) && entry.Priority == priority)
                    return true;
            }

            return false;
        }

        private bool TryReadMetadata(
            IFlowFieldVectorModifier modifier, int version,
            out Collider collider, out int priority, out int revision)
        {
            collider = null;
            priority = revision = 0;
            try
            {
                collider = modifier.InfluenceCollider;
                if (version != _operationVersion) return false;
                priority = modifier.Priority;
                if (version != _operationVersion) return false;
                revision = modifier.Revision;
                return version == _operationVersion;
            }
            catch (Exception) when (version != _operationVersion)
            {
                // A canceled getter cannot fault the replacement lifetime.
                return false;
            }
        }

        private static int CompareEntries(Entry left, Entry right)
            => left.Priority.CompareTo(right.Priority);

        private Entry Find(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                return null;

            for (int i = 0; i < _entries.Count; i++)
            {
                if (ReferenceEquals(_entries[i].Modifier, modifier))
                    return _entries[i];
            }

            return null;
        }

        private int BeginOperation()
        {
            if (IsComposing)
                throw new InvalidOperationException("Modifier registry cannot be changed while composing.");
            InvalidateOperations();
            return _operationVersion;
        }

        internal void InvalidateOperations()
        {
            unchecked { _operationVersion++; }
        }

        private static bool HasColliderSnapshotChanged(Entry entry, Collider collider)
        {
            if (!entry.SnapshotInitialized || entry.InfluenceCollider != collider)
                return true;

            if (collider == null)
                return false;

            bool active = collider.gameObject.activeInHierarchy;
            if (entry.LastEnabled != collider.enabled
                || entry.LastActive != active
                || entry.LastTrigger != collider.isTrigger)
                return true;

            Transform colliderTransform = collider.transform;
            if ((entry.LastPosition - colliderTransform.position).sqrMagnitude > 0.00000001f
                || Quaternion.Angle(entry.LastRotation, colliderTransform.rotation) > 0.0001f
                || (entry.LastScale - colliderTransform.lossyScale).sqrMagnitude > 0.00000001f)
                return true;

            Bounds bounds = collider.bounds;
            return (entry.LastBounds.center - bounds.center).sqrMagnitude > 0.00000001f
                || (entry.LastBounds.size - bounds.size).sqrMagnitude > 0.00000001f;
        }

        private FlowFieldCellRect UnionAllInfluenceRects()
        {
            FlowFieldCellRect dirty = FlowFieldCellRect.Invalid;
            for (int i = 0; i < _entries.Count; i++)
                dirty = FlowFieldCellRect.Union(
                    dirty,
                    _entries[i].InfluenceRect);
            return dirty;
        }
    }

    internal readonly struct FlowFieldModifierRegistryResult
    {
        public bool AreaDirty { get; }
        public bool ValueDirty { get; }
        public bool FinalDirty { get; }
        public FlowFieldCellRect DirtyRegion { get; }

        public FlowFieldModifierRegistryResult(
            bool areaDirty,
            bool valueDirty,
            bool finalDirty,
            FlowFieldCellRect dirtyRegion)
        {
            AreaDirty = areaDirty;
            ValueDirty = valueDirty;
            FinalDirty = finalDirty;
            DirtyRegion = dirtyRegion;
        }
    }

    /// <summary>
    /// Value snapshot and live influence collider used by one Volume build.
    /// Ordering and change observations remain owned by the registry.
    /// </summary>
    internal readonly struct FlowFieldModifierWorkItem
    {
        internal IFlowFieldModifierSnapshot Snapshot { get; }
        internal Collider InfluenceCollider { get; }

        internal FlowFieldModifierWorkItem(
            IFlowFieldModifierSnapshot snapshot,
            Collider influenceCollider)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            InfluenceCollider = influenceCollider;
        }
    }
}
