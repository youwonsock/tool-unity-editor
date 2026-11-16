using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// One registered Modifier. <see cref="Area"/> is what a space mode keeps
    /// about the cells it touches; Volume3D keeps nothing there.
    /// </summary>
    internal sealed class FlowFieldModifierEntry<TArea> where TArea : class
    {
        internal readonly IFlowFieldVectorModifier Modifier;
        internal readonly TArea Area;
        internal Collider InfluenceCollider;
        internal int Priority;
        internal int Revision;
        // The cells it touches must be found again before the next composition.
        internal bool AreaDirty = true;

        internal FlowFieldModifierEntry(IFlowFieldVectorModifier modifier, TArea area, in FlowFieldModifierObservation observation)
        {
            Modifier = modifier;
            Area = area;
            InfluenceCollider = observation.InfluenceCollider;
            Priority = observation.Priority;
            Revision = observation.Revision;
        }
    }

    /// <summary>What a notified Modifier change altered.</summary>
    internal readonly struct FlowFieldModifierChangeResult
    {
        internal readonly bool PriorityChanged;
        internal readonly bool ValueChanged;

        internal FlowFieldModifierChangeResult(bool priorityChanged, bool valueChanged)
        {
            PriorityChanged = priorityChanged;
            ValueChanged = valueChanged;
        }

        internal bool Any => PriorityChanged || ValueChanged;
    }

    /// <summary>
    /// The Modifiers of a session, the same in both space modes: registration,
    /// the configuration check, priority order (lowest first, no duplicates),
    /// change notifications and the value snapshots a build captures. Nothing
    /// observes a Modifier between its notifications. The caller enters the
    /// user-code boundary around calls that run Modifier code.
    /// </summary>
    internal sealed class FlowFieldModifierRegistry<TArea> where TArea : class
    {
        private readonly List<FlowFieldModifierEntry<TArea>> _entries = new List<FlowFieldModifierEntry<TArea>>(16);
        private readonly Func<TArea> _createArea;
        private readonly Action<TArea> _releaseArea;

        internal FlowFieldModifierRegistry(Func<TArea> createArea = null, Action<TArea> releaseArea = null)
        {
            _createArea = createArea;
            _releaseArea = releaseArea;
        }

        internal IReadOnlyList<FlowFieldModifierEntry<TArea>> Entries => _entries;
        internal int Count => _entries.Count;

        internal bool Contains(IFlowFieldVectorModifier modifier) => Find(modifier) != null;

        /// <summary>False when it is already registered.</summary>
        internal bool Register(IFlowFieldVectorModifier modifier)
        {
            if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                throw new ArgumentNullException(nameof(modifier));
            FlowFieldModifierObservation observation = modifier.Observe();
            ValidateCollider(observation.InfluenceCollider);
            if (Find(modifier) != null)
                return false;
            ThrowIfPriorityTaken(modifier, observation.Priority);
            _entries.Add(new FlowFieldModifierEntry<TArea>(modifier, _createArea?.Invoke(), in observation));
            _entries.Sort(CompareEntries);
            return true;
        }

        /// <summary>
        /// Removes it and gives the entry, whose area the caller may still read;
        /// the registry releases that area afterwards. False when it is not registered.
        /// </summary>
        internal bool Unregister(IFlowFieldVectorModifier modifier, Action<FlowFieldModifierEntry<TArea>> beforeRelease = null)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                FlowFieldModifierEntry<TArea> entry = _entries[i];
                if (!ReferenceEquals(entry.Modifier, modifier))
                    continue;
                _entries.RemoveAt(i);
                beforeRelease?.Invoke(entry);
                Release(entry);
                return true;
            }
            return false;
        }

        /// <summary>
        /// A notified change: reads the Modifier again, checks it and keeps its
        /// priority and revision. Throws for an invalid configuration, which
        /// faults the session; a Modifier that is not registered also throws.
        /// </summary>
        internal FlowFieldModifierChangeResult ApplyChange(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change)
        {
            FlowFieldModifierEntry<TArea> entry = Find(modifier)
                ?? throw new InvalidOperationException("Modifier is not registered.");
            FlowFieldModifierObservation observation = modifier.Observe();
            ValidateCollider(observation.InfluenceCollider);
            bool priorityChanged = entry.Priority != observation.Priority;
            if (priorityChanged)
                ThrowIfPriorityTaken(modifier, observation.Priority);

            entry.InfluenceCollider = observation.InfluenceCollider;
            if ((change & FlowFieldModifierChange.Area) != 0)
                entry.AreaDirty = true;
            if (priorityChanged)
            {
                entry.Priority = observation.Priority;
                _entries.Sort(CompareEntries);
            }
            bool valueChanged = (change & FlowFieldModifierChange.Value) != 0 || entry.Revision != observation.Revision;
            entry.Revision = observation.Revision;
            return new FlowFieldModifierChangeResult(priorityChanged, valueChanged);
        }

        internal void MarkAllAreasDirty()
        {
            for (int i = 0; i < _entries.Count; i++)
                _entries[i].AreaDirty = true;
        }

        /// <summary>The values a build applies, fixed when it starts.</summary>
        internal static IFlowFieldModifierSnapshot CaptureSnapshot(IFlowFieldVectorModifier modifier)
            => modifier.CaptureSnapshot()
                ?? throw new InvalidOperationException(
                    $"FlowField modifier '{modifier.GetType().Name}' returned a null snapshot.");

        /// <summary>Every Modifier's snapshot and Influence Collider, in priority order.</summary>
        internal FlowFieldModifierWorkItem[] CaptureSnapshots()
        {
            if (_entries.Count == 0)
                return Array.Empty<FlowFieldModifierWorkItem>();
            var snapshots = new FlowFieldModifierWorkItem[_entries.Count];
            for (int i = 0; i < _entries.Count; i++)
            {
                IFlowFieldVectorModifier modifier = _entries[i].Modifier;
                snapshots[i] = new FlowFieldModifierWorkItem(CaptureSnapshot(modifier), modifier.Observe().InfluenceCollider);
            }
            return snapshots;
        }

        internal void Clear()
        {
            for (int i = 0; i < _entries.Count; i++)
                Release(_entries[i]);
            _entries.Clear();
        }

        internal static void ValidateCollider(Collider collider)
        {
            if (collider == null)
                throw new InvalidOperationException("Influence Collider is required.");
            if (!collider.isTrigger)
                throw new ArgumentException(FlowFieldModifierMaskBuilder.TriggerRequiredMessage, nameof(collider));
            if (collider is MeshCollider meshCollider && !meshCollider.convex)
                throw new ArgumentException(FlowFieldModifierMaskBuilder.ConvexMeshRequiredMessage, nameof(collider));
        }

        private void ThrowIfPriorityTaken(IFlowFieldVectorModifier modifier, int priority)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (!ReferenceEquals(_entries[i].Modifier, modifier) && _entries[i].Priority == priority)
                    throw new InvalidOperationException($"Vector modifier priority {priority} is duplicated.");
            }
        }

        private void Release(FlowFieldModifierEntry<TArea> entry)
        {
            if (entry.Area != null)
                _releaseArea?.Invoke(entry.Area);
        }

        private FlowFieldModifierEntry<TArea> Find(IFlowFieldVectorModifier modifier)
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

        private static int CompareEntries(FlowFieldModifierEntry<TArea> left, FlowFieldModifierEntry<TArea> right)
            => left.Priority.CompareTo(right.Priority);
    }
}
