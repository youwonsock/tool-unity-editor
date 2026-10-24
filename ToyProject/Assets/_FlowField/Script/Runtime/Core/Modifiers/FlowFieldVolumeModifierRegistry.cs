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
        private int _captureVersion;
        private int _observationVersion;

        internal int Count => _modifiers.Count;
        internal int CaptureVersion => _captureVersion;
        internal bool Contains(IFlowFieldVectorModifier modifier)
            => modifier != null && _modifiers.Contains(modifier);

        internal void Clear()
        {
            InvalidateCaptures();
            _modifiers.Clear();
            _modifierObservations.Clear();
        }

        internal void InvalidateCaptures()
        {
            unchecked { _captureVersion++; _observationVersion++; }
        }

        internal bool DetectChanges()
        {
            int version = BeginObservation();
            if (_modifiers.Count == 0)
                return false;
            var observations = ArrayPool<ModifierObservation>.Shared.Rent(_modifiers.Count);
            try
            {
                bool changed = false;
                for (int index = _modifiers.Count - 1; index >= 0; index--)
                {
                    IFlowFieldVectorModifier modifier = _modifiers[index];
                    if (IsMissingModifier(modifier))
                    {
                        changed = true;
                        continue;
                    }
                    if (!TryReadObservation(modifier, version, out ModifierObservation current))
                        return false;
                    for (int previousIndex = _modifiers.Count - 1; previousIndex > index; previousIndex--)
                        if (!IsMissingModifier(_modifiers[previousIndex]) && observations[previousIndex].Priority == current.Priority)
                            throw new InvalidOperationException($"Vector modifier priority {current.Priority} is duplicated.");
                    observations[index] = current;
                    if (!_modifierObservations.TryGetValue(modifier, out ModifierObservation previous)
                        || !previous.Equals(current))
                        changed = true;
                }

                // Only an entire, current observation may replace the cache.
                if (changed)
                {
                    InvalidateCaptures();
                    for (int index = _modifiers.Count - 1; index >= 0; index--)
                    {
                        IFlowFieldVectorModifier modifier = _modifiers[index];
                        if (IsMissingModifier(modifier))
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
            int version = BeginObservation();
            if (!TryReadObservation(modifier, version, out ModifierObservation observation))
                return false;
            ValidateCollider(observation.Collider);
            if (_modifiers.Contains(modifier))
                return false;
            if (!TryReadOtherPriorities(modifier, observation.Priority, version, out var priorities))
                return false;
            InvalidateCaptures();
            _modifiers.Add(modifier);
            _modifierObservations[modifier] = observation;
            _modifiers.Sort((left, right) => priorities[left].CompareTo(priorities[right]));
            return true;
        }

        internal bool Unregister(IFlowFieldVectorModifier modifier)
        {
            if (!_modifiers.Remove(modifier))
                return false;
            InvalidateCaptures();
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

            int version = BeginObservation();
            try
            {
                Collider collider = modifier.InfluenceCollider;
                if (version != _observationVersion) return false;
                ValidateCollider(collider);
                if ((change & FlowFieldModifierChange.Priority) != 0)
                {
                    int priority = modifier.Priority;
                    if (version != _observationVersion) return false;
                    return TryReadOtherPriorities(modifier, priority, version, out _);
                }
                return true;
            }
            catch (Exception) when (version != _observationVersion)
            {
                return false;
            }
        }

        private int BeginObservation()
        {
            unchecked { return ++_observationVersion; }
        }

        private bool TryReadObservation(
            IFlowFieldVectorModifier modifier, int version, out ModifierObservation observation)
        {
            observation = default;
            try
            {
                Collider collider = modifier.InfluenceCollider;
                if (version != _observationVersion) return false;
                int revision = modifier.Revision;
                if (version != _observationVersion) return false;
                int priority = modifier.Priority;
                if (version != _observationVersion) return false;
                observation = ModifierObservation.From(collider, revision, priority);
                return true;
            }
            catch (Exception) when (version != _observationVersion)
            {
                return false;
            }
        }

        private bool TryReadOtherPriorities(
            IFlowFieldVectorModifier modifier, int priority, int version,
            out Dictionary<IFlowFieldVectorModifier, int> priorities)
        {
            priorities = new Dictionary<IFlowFieldVectorModifier, int>(_modifiers.Count + 1)
            {
                [modifier] = priority
            };
            try
            {
                for (int index = 0; index < _modifiers.Count; index++)
                {
                    IFlowFieldVectorModifier other = _modifiers[index];
                    if (ReferenceEquals(other, modifier))
                        continue;
                    int otherPriority = other.Priority;
                    if (version != _observationVersion) return false;
                    if (otherPriority == priority)
                        throw new InvalidOperationException($"Vector modifier priority {priority} is duplicated.");
                    priorities.Add(other, otherPriority);
                }
                return true;
            }
            catch (Exception) when (version != _observationVersion)
            {
                return false;
            }
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

            int version = _captureVersion;
            FlowFieldModifierWorkItem[] snapshots = new FlowFieldModifierWorkItem[_modifiers.Count];
            for (int index = 0; index < _modifiers.Count; index++)
            {
                IFlowFieldVectorModifier modifier = _modifiers[index];
                IFlowFieldModifierSnapshot snapshot = modifier.CaptureSnapshot();
                EnsureCurrentCapture(version);
                if (snapshot == null)
                    throw new InvalidOperationException(
                        $"FlowField modifier '{modifier.GetType().Name}' returned a null snapshot.");
                Collider collider = modifier.InfluenceCollider;
                EnsureCurrentCapture(version);
                // These getters are external code. Preserve their observation
                // order and lifetime checks without copying unused metadata
                // into every build's work items.
                _ = modifier.Priority;
                EnsureCurrentCapture(version);
                _ = modifier.Revision;
                EnsureCurrentCapture(version);
                snapshots[index] = new FlowFieldModifierWorkItem(snapshot, collider);
            }
            return snapshots;
        }

        private void EnsureCurrentCapture(int version)
        {
            if (version != _captureVersion)
                throw new OperationCanceledException("FlowField modifier capture was invalidated.");
        }

        private static bool IsMissingModifier(IFlowFieldVectorModifier modifier)
            => modifier == null
                || modifier is UnityEngine.Object unityObject && unityObject == null;

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

            internal static ModifierObservation From(Collider collider, int revision, int priority)
            {
                return new ModifierObservation(
                    collider,
                    revision,
                    priority,
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
