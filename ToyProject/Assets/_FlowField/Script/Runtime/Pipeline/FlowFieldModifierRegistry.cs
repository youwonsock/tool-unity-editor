using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>What a space mode keeps about the cells one Modifier touches.</summary>
    internal interface IFlowFieldModifierArea
    {
        void Release();
    }

    /// <summary>
    /// One registered Modifier. <see cref="Area"/> is what a space mode keeps
    /// about the cells it touches; Volume3D keeps nothing there.
    /// </summary>
    internal sealed class FlowFieldModifierEntry
    {
        internal readonly IFlowFieldVectorModifier Modifier;
        internal readonly IFlowFieldModifierArea Area;
        internal int Priority;
        internal int Revision;

        internal FlowFieldModifierEntry(IFlowFieldVectorModifier modifier, IFlowFieldModifierArea area, in FlowFieldModifierObservation observation)
        {
            Modifier = modifier;
            Area = area;
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

    internal enum FlowFieldModifierUpdateKind
    {
        Registered,
        Unregistered,
        Changed,
    }

    /// <summary>
    /// A Modifier change the session accepted, for the builder to compose
    /// again. <see cref="Entry"/> is still readable for an unregistered one.
    /// </summary>
    internal readonly struct FlowFieldModifierUpdate
    {
        internal readonly FlowFieldModifierUpdateKind Kind;
        internal readonly FlowFieldModifierEntry Entry;
        // What the notification asked for, and what it altered.
        internal readonly FlowFieldModifierChange Requested;
        internal readonly FlowFieldModifierChangeResult Result;

        internal FlowFieldModifierUpdate(
            FlowFieldModifierUpdateKind kind,
            FlowFieldModifierEntry entry,
            FlowFieldModifierChange requested = FlowFieldModifierChange.None,
            FlowFieldModifierChangeResult result = default)
        {
            Kind = kind;
            Entry = entry;
            Requested = requested;
            Result = result;
        }
    }

    /// <summary>
    /// The Modifiers of a session, the same in both space modes: registration,
    /// the configuration check, priority order (lowest first, no duplicates),
    /// change notifications and the value snapshots a build captures. Nothing
    /// observes a Modifier between its notifications. The caller enters the
    /// user-code boundary around calls that run Modifier code.
    /// </summary>
    internal sealed class FlowFieldModifierRegistry
    {
        private readonly List<FlowFieldModifierEntry> _entries = new List<FlowFieldModifierEntry>(16);
        private readonly Func<IFlowFieldModifierArea> _createArea;

        internal FlowFieldModifierRegistry(Func<IFlowFieldModifierArea> createArea)
        {
            _createArea = createArea;
        }

        internal IReadOnlyList<FlowFieldModifierEntry> Entries => _entries;
        internal int Count => _entries.Count;

        internal bool Contains(IFlowFieldVectorModifier modifier) => Find(modifier) != null;

        internal FlowFieldModifierEntry Get(IFlowFieldVectorModifier modifier) => Find(modifier);

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
            _entries.Add(new FlowFieldModifierEntry(modifier, _createArea?.Invoke(), in observation));
            _entries.Sort(CompareEntries);
            return true;
        }

        /// <summary>
        /// Removes it and gives the entry, whose area the caller may still read;
        /// the registry releases that area afterwards. False when it is not registered.
        /// </summary>
        internal bool Unregister(IFlowFieldVectorModifier modifier, Action<FlowFieldModifierEntry> beforeRelease = null)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                FlowFieldModifierEntry entry = _entries[i];
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
            FlowFieldModifierEntry entry = Find(modifier)
                ?? throw new InvalidOperationException("Modifier is not registered.");
            FlowFieldModifierObservation observation = modifier.Observe();
            ValidateCollider(observation.InfluenceCollider);
            bool priorityChanged = entry.Priority != observation.Priority;
            if (priorityChanged)
                ThrowIfPriorityTaken(modifier, observation.Priority);

            if (priorityChanged)
            {
                entry.Priority = observation.Priority;
                _entries.Sort(CompareEntries);
            }
            bool valueChanged = (change & FlowFieldModifierChange.Value) != 0 || entry.Revision != observation.Revision;
            entry.Revision = observation.Revision;
            return new FlowFieldModifierChangeResult(priorityChanged, valueChanged);
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

        /// <summary>Throws the reason an Influence Collider breaks the rule.</summary>
        internal static void ValidateCollider(Collider collider)
        {
            if (FlowFieldModifierMaskBuilder.TryValidateInfluenceCollider(collider, out string reason))
                return;
            if (collider == null)
                throw new InvalidOperationException(reason);
            throw new ArgumentException(reason, nameof(collider));
        }

        private void ThrowIfPriorityTaken(IFlowFieldVectorModifier modifier, int priority)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (!ReferenceEquals(_entries[i].Modifier, modifier) && _entries[i].Priority == priority)
                    throw new InvalidOperationException(DuplicatePriorityMessage(priority));
            }
        }

        /// <summary>Why a Modifier cannot register: the Editor warns with the same words.</summary>
        internal static string DuplicatePriorityMessage(int priority)
            => $"Vector modifier priority {priority} is duplicated.";

        private static void Release(FlowFieldModifierEntry entry) => entry.Area?.Release();

        private FlowFieldModifierEntry Find(IFlowFieldVectorModifier modifier)
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

        private static int CompareEntries(FlowFieldModifierEntry left, FlowFieldModifierEntry right)
            => left.Priority.CompareTo(right.Priority);
    }
}
