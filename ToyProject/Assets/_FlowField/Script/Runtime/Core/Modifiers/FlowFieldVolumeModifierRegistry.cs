using System;
using System.Buffers;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Owns Volume3D modifier registration, observations and immutable value snapshots.
    /// It reports effective input changes; the session retains request and lifetime policy.
    /// Collider observations deliberately remain live, matching the existing build contract.
    /// </summary>
    internal sealed class FlowFieldVolumeModifierRegistry
    {
        private readonly List<IFlowFieldVectorModifier> _modifiers = new List<IFlowFieldVectorModifier>(16);
        private readonly Dictionary<IFlowFieldVectorModifier, ModifierObservation> _modifierObservations
            = new Dictionary<IFlowFieldVectorModifier, ModifierObservation>(16);

        internal int Count => _modifiers.Count;
        internal bool Contains(IFlowFieldVectorModifier modifier)
            => modifier != null && _modifiers.Contains(modifier);

        internal void Clear()
        {
            _modifiers.Clear();
            _modifierObservations.Clear();
        }

        internal bool DetectChanges()
        {
            if (_modifiers.Count == 0)
                return false;
            var observations = ArrayPool<ModifierObservation>.Shared.Rent(_modifiers.Count);
            try
            {
                bool changed = false;
                for (int index = _modifiers.Count - 1; index >= 0; index--)
                {
                    IFlowFieldVectorModifier modifier = _modifiers[index];
                    if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                    {
                        changed = true;
                        continue;
                    }
                    ModifierObservation current = ModifierObservation.From(modifier.Observe());
                    for (int previousIndex = _modifiers.Count - 1; previousIndex > index; previousIndex--)
                        if (!FlowFieldVectorUtility.IsMissingModifier(_modifiers[previousIndex]) && observations[previousIndex].Priority == current.Priority)
                            throw new InvalidOperationException($"Vector modifier priority {current.Priority} is duplicated.");
                    observations[index] = current;
                    if (!_modifierObservations.TryGetValue(modifier, out ModifierObservation previous)
                        || !previous.Equals(current))
                        changed = true;
                }

                // Only a complete observation may replace the cache.
                if (changed)
                {
                    for (int index = _modifiers.Count - 1; index >= 0; index--)
                    {
                        IFlowFieldVectorModifier modifier = _modifiers[index];
                        if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                        {
                            _modifiers.RemoveAt(index);
                            _modifierObservations.Remove(modifier);
                        }
                        else
                            _modifierObservations[modifier] = observations[index];
                    }
                    _modifiers.Sort((left, right) => _modifierObservations[left].Priority.CompareTo(_modifierObservations[right].Priority));
                }
                return changed;
            }
            finally
            {
                ArrayPool<ModifierObservation>.Shared.Return(observations, clearArray: true);
            }
        }

        internal bool Register(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            ModifierObservation observation = ModifierObservation.From(modifier.Observe());
            ValidateCollider(observation.Collider);
            if (_modifiers.Contains(modifier))
                return false;
            Dictionary<IFlowFieldVectorModifier, int> priorities = ReadOtherPriorities(modifier, observation.Priority);
            _modifiers.Add(modifier);
            _modifierObservations[modifier] = observation;
            _modifiers.Sort((left, right) => priorities[left].CompareTo(priorities[right]));
            return true;
        }

        internal bool Unregister(IFlowFieldVectorModifier modifier)
        {
            if (!_modifiers.Remove(modifier))
                return false;
            _modifierObservations.Remove(modifier);
            return true;
        }

        internal bool ValidateModifierChange(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierChange change)
        {
            if (modifier == null || !_modifiers.Contains(modifier))
                throw new InvalidOperationException("Modifier is not registered.");
            if ((change & (FlowFieldModifierChange.Value
                | FlowFieldModifierChange.Area
                | FlowFieldModifierChange.Priority)) == 0)
                return true;

            FlowFieldModifierObservation observation = modifier.Observe();
            ValidateCollider(observation.InfluenceCollider);
            if ((change & FlowFieldModifierChange.Priority) != 0)
                ReadOtherPriorities(modifier, observation.Priority);
            return true;
        }

        private Dictionary<IFlowFieldVectorModifier, int> ReadOtherPriorities(
            IFlowFieldVectorModifier modifier,
            int priority)
        {
            var priorities = new Dictionary<IFlowFieldVectorModifier, int>(_modifiers.Count + 1)
            {
                [modifier] = priority
            };
            for (int index = 0; index < _modifiers.Count; index++)
            {
                IFlowFieldVectorModifier other = _modifiers[index];
                if (ReferenceEquals(other, modifier))
                    continue;
                int otherPriority = other.Observe().Priority;
                if (otherPriority == priority)
                    throw new InvalidOperationException($"Vector modifier priority {priority} is duplicated.");
                priorities.Add(other, otherPriority);
            }
            return priorities;
        }

        private static void ValidateCollider(Collider collider)
        {
            if (collider == null)
                throw new InvalidOperationException("Influence Collider is required.");
            if (!collider.isTrigger)
                throw new ArgumentException(FlowFieldModifierMaskBuilder.TriggerRequiredMessage, "modifier");
            if (collider is MeshCollider meshCollider && !meshCollider.convex)
                throw new ArgumentException(FlowFieldModifierMaskBuilder.ConvexMeshRequiredMessage, "modifier");
        }

        internal FlowFieldModifierWorkItem[] CaptureSnapshots()
        {
            if (_modifiers.Count == 0)
                return Array.Empty<FlowFieldModifierWorkItem>();

            FlowFieldModifierWorkItem[] snapshots = new FlowFieldModifierWorkItem[_modifiers.Count];
            for (int index = 0; index < _modifiers.Count; index++)
            {
                IFlowFieldVectorModifier modifier = _modifiers[index];
                IFlowFieldModifierSnapshot snapshot = modifier.CaptureSnapshot();
                if (snapshot == null)
                    throw new InvalidOperationException(
                        $"FlowField modifier '{modifier.GetType().Name}' returned a null snapshot.");
                snapshots[index] = new FlowFieldModifierWorkItem(snapshot, modifier.Observe().InfluenceCollider);
            }
            return snapshots;
        }

        private readonly struct ModifierObservation : IEquatable<ModifierObservation>
        {
            private readonly Collider _collider;
            private readonly int _revision;
            private readonly int _priority;
            private readonly Bounds _bounds;
            private readonly bool _enabled;
            private readonly bool _active;
            private readonly bool _trigger;

            private ModifierObservation(
                Collider collider,
                int revision,
                int priority,
                Bounds bounds,
                bool enabled,
                bool active,
                bool trigger)
            {
                _collider = collider;
                _revision = revision;
                _priority = priority;
                _bounds = bounds;
                _enabled = enabled;
                _active = active;
                _trigger = trigger;
            }

            internal Collider Collider => _collider;
            internal int Priority => _priority;

            internal static ModifierObservation From(in FlowFieldModifierObservation observation)
            {
                Collider collider = observation.InfluenceCollider;
                return new ModifierObservation(
                    collider,
                    observation.Revision,
                    observation.Priority,
                    collider != null ? collider.bounds : default,
                    collider != null && collider.enabled,
                    collider != null && collider.gameObject.activeInHierarchy,
                    collider != null && collider.isTrigger);
            }

            public bool Equals(ModifierObservation other)
                => _collider == other._collider
                    && _revision == other._revision
                    && _priority == other._priority
                    && _enabled == other._enabled
                    && _active == other._active
                    && _trigger == other._trigger
                    && (_bounds.center - other._bounds.center).sqrMagnitude <= 0.00000001f
                    && (_bounds.size - other._bounds.size).sqrMagnitude <= 0.00000001f;

            public override bool Equals(object obj)
                => obj is ModifierObservation other && Equals(other);

            public override int GetHashCode()
                => 0;
        }
    }
}
