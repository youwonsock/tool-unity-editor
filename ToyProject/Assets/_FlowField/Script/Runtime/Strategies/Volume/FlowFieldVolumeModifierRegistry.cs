using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Owns Volume3D modifier registration, priority order and the value
    /// snapshots a build captures. Changes arrive as notifications; nothing
    /// here observes the Modifiers between them.
    /// </summary>
    internal sealed class FlowFieldVolumeModifierRegistry
    {
        private readonly List<IFlowFieldVectorModifier> _modifiers = new List<IFlowFieldVectorModifier>(16);

        internal int Count => _modifiers.Count;

        internal void Clear()
        {
            _modifiers.Clear();
        }

        internal bool Register(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            FlowFieldModifierObservation observation = modifier.Observe();
            ValidateCollider(observation.InfluenceCollider);
            if (_modifiers.Contains(modifier))
                return false;
            Dictionary<IFlowFieldVectorModifier, int> priorities = ReadOtherPriorities(modifier, observation.Priority);
            _modifiers.Add(modifier);
            _modifiers.Sort((left, right) => priorities[left].CompareTo(priorities[right]));
            return true;
        }

        internal bool Unregister(IFlowFieldVectorModifier modifier)
        {
            return _modifiers.Remove(modifier);
        }

        internal bool Contains(IFlowFieldVectorModifier modifier)
            => modifier != null && _modifiers.Contains(modifier);

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
    }
}
