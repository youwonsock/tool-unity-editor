using System;
using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.FlowField
{
    internal sealed partial class FlowFieldModifierRegistry
    {
        private readonly List<Entry> _entries = new List<Entry>(16);
        private readonly HashSet<IFlowFieldVectorModifier> _faultedModifiers = new HashSet<IFlowFieldVectorModifier>();
        private readonly HashSet<IFlowFieldVectorModifier> _invalidWarnings = new HashSet<IFlowFieldVectorModifier>();
        private readonly HashSet<int> _duplicatePriorityWarnings = new HashSet<int>();
        private readonly List<RegistrationChange> _pendingChanges = new List<RegistrationChange>(8);
        private readonly List<FlowFieldModifierDiagnostic> _diagnostics = new List<FlowFieldModifierDiagnostic>(4);
        private long _nextRegistrationOrder;

#if UNITY_EDITOR
        private readonly HashSet<IFlowFieldVectorModifier> _editorFaultedModifiers = new HashSet<IFlowFieldVectorModifier>();
#endif

        private readonly struct RegistrationChange
        {
            public IFlowFieldVectorModifier Modifier { get; }
            public bool Register { get; }

            public RegistrationChange(IFlowFieldVectorModifier modifier, bool register)
            {
                Modifier = modifier;
                Register = register;
            }
        }

        internal IReadOnlyList<Entry> Entries => _entries;
        internal IReadOnlyList<FlowFieldModifierDiagnostic> Diagnostics => _diagnostics;
        internal bool IsComposing { get; private set; }

        internal FlowFieldModifierRegistryResult Register(IFlowFieldVectorModifier modifier)
        {
            BeginOperation();
            if (IsMissingModifier(modifier))
                return default;

            if (IsComposing)
            {
                _pendingChanges.Add(new RegistrationChange(modifier, true));
                return default;
            }

            return RegisterImmediately(modifier);
        }

        internal FlowFieldModifierRegistryResult Unregister(IFlowFieldVectorModifier modifier)
        {
            BeginOperation();
            if (modifier == null)
                return default;

            if (IsComposing)
            {
                _pendingChanges.Add(new RegistrationChange(modifier, false));
                return default;
            }

            return UnregisterImmediately(modifier);
        }

        internal FlowFieldModifierRegistryResult MarkDirty(IFlowFieldVectorModifier modifier)
        {
            BeginOperation();
            Entry entry = Find(modifier);
            if (entry == null)
                return default;

            bool finalDirty = false;
            bool valueDirty = false;
            if (TryReadMetadata(modifier, out Collider collider, out int priority, out int revision, out Exception exception))
            {
                entry.InfluenceCollider = collider;
                if (entry.Priority != priority)
                {
                    entry.Priority = priority;
                    SortAndDiagnoseDuplicates();
                    finalDirty = true;
                }

                if (entry.Revision != revision)
                {
                    entry.Revision = revision;
                    valueDirty = true;
                    finalDirty = true;
                }
            }
            else
            {
                AddAccessDiagnostic(modifier, exception);
                finalDirty = true;
            }

            return new FlowFieldModifierRegistryResult(false, valueDirty, finalDirty);
        }

        internal FlowFieldModifierRegistryResult MarkAreaDirty(IFlowFieldVectorModifier modifier)
        {
            BeginOperation();
            Entry entry = Find(modifier);
            if (entry == null)
                return default;

            entry.AreaDirty = true;
            return new FlowFieldModifierRegistryResult(true, false, false);
        }

        internal FlowFieldModifierRegistryResult DetectChanges()
        {
            BeginOperation();
            bool areaDirty = false;
            bool valueDirty = false;
            bool finalDirty = false;
            bool priorityChanged = false;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                IFlowFieldVectorModifier modifier = entry.Modifier;
                if (IsMissingModifier(modifier))
                {
                    _entries.RemoveAt(i);
                    finalDirty = true;
                    continue;
                }

                if (!TryReadMetadata(modifier, out Collider collider, out int priority, out int revision, out Exception exception))
                {
                    AddAccessDiagnostic(modifier, exception);
                    finalDirty = true;
                    continue;
                }

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
                    entry.InfluenceCollider = collider;
                    entry.AreaDirty = true;
                    areaDirty = true;
                }

                if (collider == null || !collider.isTrigger)
                    AddInvalidDiagnosticOnce(modifier, "Influence Collider는 활성 Trigger여야 합니다.");
                else if (collider is MeshCollider meshCollider && !meshCollider.convex)
                    AddInvalidDiagnosticOnce(modifier, FlowFieldModifierMaskBuilder.ConvexMeshRequiredMessage);
                else
                    _invalidWarnings.Remove(modifier);
            }

            if (priorityChanged)
                SortAndDiagnoseDuplicates();

            return new FlowFieldModifierRegistryResult(areaDirty, valueDirty, finalDirty);
        }

        internal FlowFieldModifierRegistryResult FlushPendingChanges()
        {
            if (IsComposing || _pendingChanges.Count == 0)
                return default;

            BeginOperation();

            bool areaDirty = false;
            bool valueDirty = false;
            bool finalDirty = false;
            for (int i = 0; i < _pendingChanges.Count; i++)
            {
                RegistrationChange change = _pendingChanges[i];
                FlowFieldModifierRegistryResult result = change.Register
                    ? RegisterImmediately(change.Modifier)
                    : UnregisterImmediately(change.Modifier);
                areaDirty |= result.AreaDirty;
                valueDirty |= result.ValueDirty;
                finalDirty |= result.FinalDirty;
            }

            _pendingChanges.Clear();
            return new FlowFieldModifierRegistryResult(areaDirty, valueDirty, finalDirty);
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

        internal bool HasDuplicate(IFlowFieldVectorModifier modifier)
        {
            Entry target = Find(modifier);
            if (target == null)
                return false;

            for (int i = 0; i < _entries.Count; i++)
            {
                Entry other = _entries[i];
                if (!ReferenceEquals(other, target) && other.Priority == target.Priority)
                    return true;
            }

            return false;
        }

        internal bool MarkRuntimeFaulted(IFlowFieldVectorModifier modifier, Exception exception)
        {
            return AddFaultedDiagnostic(
                modifier,
                FlowFieldModifierDiagnosticKind.RuntimeException,
                exception);
        }

        internal bool ReportAccessException(IFlowFieldVectorModifier modifier, Exception exception)
            => AddFaultedDiagnostic(modifier, FlowFieldModifierDiagnosticKind.AccessException, exception);

#if UNITY_EDITOR
        internal bool MarkEditorFaulted(IFlowFieldVectorModifier modifier, Exception exception)
        {
            return AddEditorFaultedDiagnostic(modifier, exception);
        }

        internal bool ReportEditorAccessException(IFlowFieldVectorModifier modifier, Exception exception)
            => AddEditorFaultedDiagnostic(modifier, exception);

        internal bool IsEditorFaulted(IFlowFieldVectorModifier modifier)
            => _editorFaultedModifiers.Contains(modifier);
#endif

        internal bool IsFaulted(IFlowFieldVectorModifier modifier)
            => _faultedModifiers.Contains(modifier);

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
            _faultedModifiers.Clear();
            _invalidWarnings.Clear();
            _duplicatePriorityWarnings.Clear();
            _pendingChanges.Clear();
            _diagnostics.Clear();
#if UNITY_EDITOR
            _editorFaultedModifiers.Clear();
#endif
            IsComposing = false;
        }

        internal void ClearDiagnostics()
            => _diagnostics.Clear();

        private FlowFieldModifierRegistryResult RegisterImmediately(IFlowFieldVectorModifier modifier)
        {
            if (Find(modifier) != null)
                return default;

            if (!TryReadMetadata(modifier, out Collider collider, out int priority, out int revision, out Exception exception))
            {
                AddAccessDiagnostic(modifier, exception);
                return default;
            }

            if (!ValidateConfiguration(modifier, collider))
                return default;

            _invalidWarnings.Remove(modifier);
            _faultedModifiers.Remove(modifier);
#if UNITY_EDITOR
            _editorFaultedModifiers.Remove(modifier);
#endif
            _entries.Add(new Entry(modifier, _nextRegistrationOrder++, collider, priority, revision));
            SortAndDiagnoseDuplicates();
            return new FlowFieldModifierRegistryResult(true, false, true);
        }

        private FlowFieldModifierRegistryResult UnregisterImmediately(IFlowFieldVectorModifier modifier)
        {
            bool removed = false;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_entries[i].Modifier, modifier))
                    continue;

                _entries.RemoveAt(i);
                removed = true;
            }

            _faultedModifiers.Remove(modifier);
            _invalidWarnings.Remove(modifier);
#if UNITY_EDITOR
            _editorFaultedModifiers.Remove(modifier);
#endif
            return removed
                ? new FlowFieldModifierRegistryResult(false, false, true)
                : default;
        }

        private bool ValidateConfiguration(IFlowFieldVectorModifier modifier, Collider collider)
        {
            if (collider == null)
            {
                AddInvalidDiagnosticOnce(modifier, "Influence Collider가 없어 등록할 수 없습니다.");
                return false;
            }

            if (!collider.isTrigger)
            {
                AddInvalidDiagnosticOnce(modifier, FlowFieldModifierMaskBuilder.TriggerRequiredMessage);
                return false;
            }

            if (collider is MeshCollider meshCollider && !meshCollider.convex)
            {
                AddInvalidDiagnosticOnce(modifier, FlowFieldModifierMaskBuilder.ConvexMeshRequiredMessage);
                return false;
            }

            return true;
        }

        private void SortAndDiagnoseDuplicates()
        {
            _entries.Sort(CompareEntries);
            for (int i = 1; i < _entries.Count; i++)
            {
                int priority = _entries[i].Priority;
                if (_entries[i - 1].Priority != priority || !_duplicatePriorityWarnings.Add(priority))
                    continue;

                _diagnostics.Add(new FlowFieldModifierDiagnostic(
                    FlowFieldModifierDiagnosticKind.DuplicatePriority,
                    _entries[i].Modifier,
                    priority: priority));
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

        private bool TryReadMetadata(
            IFlowFieldVectorModifier modifier,
            out Collider collider,
            out int priority,
            out int revision,
            out Exception exception)
        {
            collider = null;
            priority = 0;
            revision = 0;
            exception = null;
            try
            {
                collider = modifier.InfluenceCollider;
                priority = modifier.Priority;
                revision = modifier.Revision;
                return true;
            }
            catch (Exception caught)
            {
                exception = caught;
                return false;
            }
        }

        private void AddInvalidDiagnosticOnce(IFlowFieldVectorModifier modifier, string message)
        {
            if (!_invalidWarnings.Add(modifier))
                return;

            _diagnostics.Add(new FlowFieldModifierDiagnostic(
                FlowFieldModifierDiagnosticKind.InvalidConfiguration,
                modifier,
                message: message));
        }

        private void AddAccessDiagnostic(IFlowFieldVectorModifier modifier, Exception exception)
        {
            AddFaultedDiagnostic(modifier, FlowFieldModifierDiagnosticKind.AccessException, exception);
        }

        private bool AddFaultedDiagnostic(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierDiagnosticKind kind,
            Exception exception)
        {
            if (!_faultedModifiers.Add(modifier))
                return false;

            _diagnostics.Add(new FlowFieldModifierDiagnostic(kind, modifier, exception: exception));
            return true;
        }

#if UNITY_EDITOR
        private bool AddEditorFaultedDiagnostic(IFlowFieldVectorModifier modifier, Exception exception)
        {
            if (!_editorFaultedModifiers.Add(modifier))
                return false;

            _diagnostics.Add(new FlowFieldModifierDiagnostic(
                FlowFieldModifierDiagnosticKind.EditorException,
                modifier,
                exception: exception));
            return true;
        }
#endif

        private void BeginOperation()
            => _diagnostics.Clear();

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
    }
}
