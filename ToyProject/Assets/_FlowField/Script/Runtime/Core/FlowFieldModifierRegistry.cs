using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal sealed class FlowFieldModifierRegistry
    {
        internal sealed class Entry
        {
            internal readonly IFlowFieldVectorModifier Modifier;
            internal readonly long RegistrationOrder;
            internal bool[] InfluenceMask;
            internal bool[] InfluenceScratch;
            internal readonly List<int> InfluenceIndices = new List<int>(64);
            internal bool InfluenceIndicesBuilt;
            internal Collider InfluenceCollider;
            internal int Priority;
            internal int Revision;
            internal bool AreaDirty = true;
            internal bool SnapshotInitialized;
            internal bool LastEnabled;
            internal bool LastActive;
            internal bool LastTrigger;
            internal Vector3 LastPosition;
            internal Quaternion LastRotation;
            internal Vector3 LastScale;
            internal Bounds LastBounds;
            internal FlowFieldCellRect InfluenceRect = FlowFieldCellRect.Invalid;

            internal Entry(
                IFlowFieldVectorModifier modifier,
                long registrationOrder,
                Collider influenceCollider,
                int priority,
                int revision)
            {
                Modifier = modifier;
                RegistrationOrder = registrationOrder;
                InfluenceCollider = influenceCollider;
                Priority = priority;
                Revision = revision;
            }
        }

        private readonly List<Entry> _entries = new List<Entry>(16);
        private long _nextRegistrationOrder;

        internal IReadOnlyList<Entry> Entries => _entries;
        internal bool IsComposing { get; private set; }

        internal FlowFieldModifierRegistryResult Register(IFlowFieldVectorModifier modifier)
        {
            if (IsMissingModifier(modifier))
                throw new ArgumentNullException(nameof(modifier));
            BeginOperation();

            if (IsComposing)
                throw new InvalidOperationException("Modifier registration is not allowed during composition.");

            return RegisterImmediately(modifier);
        }

        internal FlowFieldModifierRegistryResult Unregister(IFlowFieldVectorModifier modifier)
        {
            if (IsMissingModifier(modifier))
                throw new ArgumentNullException(nameof(modifier));
            BeginOperation();

            if (IsComposing)
                throw new InvalidOperationException("Modifier unregistration is not allowed during composition.");

            return UnregisterImmediately(modifier);
        }

        internal FlowFieldModifierRegistryResult MarkDirty(IFlowFieldVectorModifier modifier)
        {
            if (IsMissingModifier(modifier))
                throw new ArgumentNullException(nameof(modifier));
            BeginOperation();
            Entry entry = Find(modifier);
            if (entry == null)
                throw new InvalidOperationException("Modifier is not registered.");

            bool finalDirty = false;
            bool valueDirty = false;
            bool priorityChanged = false;
            Collider collider = modifier.InfluenceCollider;
            int priority = modifier.Priority;
            int revision = modifier.Revision;
            ValidateConfiguration(modifier, collider);
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
            if (entry.Revision != revision)
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
            if (IsMissingModifier(modifier))
                throw new ArgumentNullException(nameof(modifier));
            BeginOperation();
            Entry entry = Find(modifier);
            if (entry == null)
                throw new InvalidOperationException("Modifier is not registered.");

            entry.AreaDirty = true;
            return new FlowFieldModifierRegistryResult(true, false, false, entry.InfluenceRect);
        }

        internal FlowFieldModifierRegistryResult DetectChanges()
        {
            BeginOperation();
            ValidateCurrentConfigurationsAndPriorities();
            bool areaDirty = false;
            bool valueDirty = false;
            bool finalDirty = false;
            bool priorityChanged = false;
            FlowFieldCellRect dirtyRegion = FlowFieldCellRect.Invalid;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                IFlowFieldVectorModifier modifier = entry.Modifier;
                if (IsMissingModifier(modifier))
                {
                    dirtyRegion = FlowFieldCellRect.Union(dirtyRegion, entry.InfluenceRect);
                    _entries.RemoveAt(i);
                    finalDirty = true;
                    continue;
                }

                Collider collider = modifier.InfluenceCollider;
                int priority = modifier.Priority;
                int revision = modifier.Revision;

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

        internal FlowFieldModifierRegistryResult FlushPendingChanges()
        {
            if (IsComposing)
                throw new InvalidOperationException("Pending modifier changes are not supported during composition.");
            return default;
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
            _entries.Clear();
            IsComposing = false;
        }

        private FlowFieldModifierRegistryResult RegisterImmediately(IFlowFieldVectorModifier modifier)
        {
            if (Find(modifier) != null)
                throw new InvalidOperationException("Modifier is already registered.");

            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            Collider collider = modifier.InfluenceCollider;
            int priority = modifier.Priority;
            int revision = modifier.Revision;

            ValidateConfiguration(modifier, collider);
            if (HasPriorityConflict(modifier, priority))
                throw new InvalidOperationException($"Vector modifier priority {priority} is duplicated.");

            Entry entry = new Entry(modifier, _nextRegistrationOrder++, collider, priority, revision);
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

                dirty = FlowFieldCellRect.Union(dirty, _entries[i].InfluenceRect);
                _entries.RemoveAt(i);
                removed = true;
            }

            if (!removed)
                throw new InvalidOperationException("Modifier is not registered.");
            return new FlowFieldModifierRegistryResult(false, false, true, dirty);
        }

        private void ValidateConfiguration(IFlowFieldVectorModifier modifier, Collider collider)
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

        private void ValidateCurrentConfigurationsAndPriorities()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry entry = _entries[i];
                IFlowFieldVectorModifier modifier = entry.Modifier;
                if (IsMissingModifier(modifier))
                    continue;

                ValidateConfiguration(modifier, modifier.InfluenceCollider);
                int priority = modifier.Priority;
                for (int j = 0; j < i; j++)
                {
                    IFlowFieldVectorModifier previous = _entries[j].Modifier;
                    if (!IsMissingModifier(previous) && previous.Priority == priority)
                        throw new InvalidOperationException($"Vector modifier priority {priority} is duplicated.");
                }
            }
        }

        private static int CompareEntries(Entry left, Entry right)
        {
            int priorityComparison = left.Priority.CompareTo(right.Priority);
            return priorityComparison != 0
                ? priorityComparison
                : left.RegistrationOrder.CompareTo(right.RegistrationOrder);
        }

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

        private void BeginOperation()
        {
            if (IsComposing)
                throw new InvalidOperationException("Modifier registry cannot be changed while composing.");
        }

        private static bool IsMissingModifier(IFlowFieldVectorModifier modifier)
            => modifier == null || modifier is UnityEngine.Object unityObject && unityObject == null;

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
                dirty = FlowFieldCellRect.Union(dirty, _entries[i].InfluenceRect);
            return dirty;
        }
    }

    internal readonly struct FlowFieldModifierRegistryResult
    {
        public bool AreaDirty { get; }
        public bool ValueDirty { get; }
        public bool FinalDirty { get; }
        public FlowFieldCellRect DirtyRegion { get; }

        public FlowFieldModifierRegistryResult(bool areaDirty, bool valueDirty, bool finalDirty)
            : this(areaDirty, valueDirty, finalDirty, FlowFieldCellRect.Invalid)
        {
        }

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
}
