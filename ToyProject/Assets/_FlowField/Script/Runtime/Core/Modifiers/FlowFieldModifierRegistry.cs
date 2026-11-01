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

        internal IReadOnlyList<Entry> Entries => _entries;

        internal FlowFieldModifierRegistryResult Register(IFlowFieldVectorModifier modifier)
        {
            if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                throw new ArgumentNullException(nameof(modifier));
            return RegisterImmediately(modifier);
        }

        internal FlowFieldModifierRegistryResult Unregister(IFlowFieldVectorModifier modifier)
        {
            if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                throw new ArgumentNullException(nameof(modifier));
            return UnregisterImmediately(modifier);
        }

        internal FlowFieldModifierRegistryResult MarkDirty(
            IFlowFieldVectorModifier modifier,
            bool valueNotified)
        {
            if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                throw new ArgumentNullException(nameof(modifier));
            Entry entry = Find(modifier);
            if (entry == null)
                throw new InvalidOperationException("Modifier is not registered.");

            bool finalDirty = false;
            bool valueDirty = false;
            bool priorityChanged = false;
            FlowFieldModifierObservation observation = modifier.Observe();
            Collider collider = observation.InfluenceCollider;
            int priority = observation.Priority;
            int revision = observation.Revision;
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
            return new FlowFieldModifierRegistryResult(false, valueDirty, finalDirty, dirty);
        }

        internal FlowFieldModifierRegistryResult MarkAreaDirty(IFlowFieldVectorModifier modifier)
        {
            if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                throw new ArgumentNullException(nameof(modifier));
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
            // No cache changes until every Modifier has answered, so one that
            // throws leaves the earlier observations in place.
            if (_entries.Count == 0)
                return default;
            var observations = ArrayPool<FlowFieldModifierObservation>.Shared.Rent(_entries.Count);
            try
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    IFlowFieldVectorModifier modifier = _entries[i].Modifier;
                    if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                        continue;
                    FlowFieldModifierObservation observation = modifier.Observe();
                    ValidateConfiguration(observation.InfluenceCollider);
                    for (int previous = 0; previous < i; previous++)
                        if (!FlowFieldVectorUtility.IsMissingModifier(_entries[previous].Modifier) && observations[previous].Priority == observation.Priority)
                            throw new InvalidOperationException($"Vector modifier priority {observation.Priority} is duplicated.");
                    observations[i] = observation;
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

                    Collider collider = observations[i].InfluenceCollider;
                    int priority = observations[i].Priority;
                    int revision = observations[i].Revision;

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
                ArrayPool<FlowFieldModifierObservation>.Shared.Return(observations, clearArray: true);
            }
        }

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
            for (int i = 0; i < _entries.Count; i++)
                _entries[i].ReleaseArea();
            _entries.Clear();
        }

        private FlowFieldModifierRegistryResult RegisterImmediately(IFlowFieldVectorModifier modifier)
        {
            if (Find(modifier) != null)
                throw new InvalidOperationException("Modifier is already registered.");

            FlowFieldModifierObservation observation = modifier.Observe();
            Collider collider = observation.InfluenceCollider;
            ValidateConfiguration(collider);
            if (HasPriorityConflict(modifier, observation.Priority))
                throw new InvalidOperationException($"Vector modifier priority {observation.Priority} is duplicated.");

            Entry entry = new Entry(modifier, collider, observation.Priority, observation.Revision);
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
