using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>The Provider and Controller API and the checks every call makes first.</summary>
    public partial class FlowFieldManager
    {
        private void ThrowIfLifecycleUnavailable()
        {
            if (IsFaulted)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} is faulted.", ActiveSession.Fault);
            if (!IsInitialized)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} is not initialized.");
        }

        private void ThrowIfAvailableForInput()
        {
            ThrowIfLifecycleUnavailable();
            if (ActiveMode.ConfigurationStale)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} configuration is stale. Call RequestRebuild() before use.");
        }

        private void ThrowIfInputAllowedForMode()
        {
            ThrowIfLifecycleUnavailable();
            // StaticBaked ignores runtime Goal/obstacle input. It must remain
            // a valid no-op even while an unrelated serialized setting is
            // awaiting an explicit rebuild.
            if (ActiveMode.ConfigurationStale && BakeMode != FlowFieldBakeMode.StaticBaked)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} configuration is stale. Call RequestRebuild() before use.");
        }

        private void MarkGoalDirty()
            => ActiveMode.MarkGoalDirty();

        public FlowFieldSample Sample(Vector3 worldPosition)
        {
            ThrowIfAvailableForInput();
            return ActiveSession.Sample(worldPosition);
        }

        public bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            return ActiveSession?.TrySample(worldPosition, out sample) ?? false;
        }

        public FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            ThrowIfAvailableForInput();
            return ActiveSession.ClampPositionToGrid(worldPosition);
        }

        public void SetSpaceMode(FlowFieldSpaceMode mode)
        {
            _boundary.ThrowIfInUserCode(nameof(SetSpaceMode));
            if (!Enum.IsDefined(typeof(FlowFieldSpaceMode), mode))
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (IsInitialized || IsFaulted)
                throw new InvalidOperationException("FlowField Space Mode can only change after Release and before Init.");
            _spaceMode = mode;
        }

        public bool RegisterDynamicObstacle(Collider collider)
        {
            ThrowIfInputAllowedForMode();
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (collider == null)
                throw new InvalidOperationException("Cannot register a destroyed Collider.");
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw new InvalidOperationException("Dynamic obstacle registration is not allowed in StaticBaked mode.");
            return ActiveSession.RegisterObstacleCommand(collider);
        }

        public bool UnregisterDynamicObstacle(Collider collider)
        {
            // Removal needs no valid configuration, as with Modifiers.
            ThrowIfLifecycleUnavailable();
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;
            return ActiveSession.UnregisterObstacleCommand(collider);
        }

        public void NotifyObstacleRegionDirty(Bounds worldBounds)
        {
            _boundary.ThrowIfInUserCode(nameof(NotifyObstacleRegionDirty));
            ThrowIfInputAllowedForMode();
            if (!FlowFieldGridSpace.IsFinite(worldBounds.center)
                || !FlowFieldGridSpace.IsFinite(worldBounds.size))
                throw new ArgumentOutOfRangeException(nameof(worldBounds));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw new InvalidOperationException("Obstacle changes are not allowed in StaticBaked mode.");
            ActiveSession.NotifyObstacleRegionDirtyCommand(worldBounds);
        }

        public void SetGoal(in FlowFieldGoalRequest request)
        {
            _boundary.ThrowIfInUserCode(nameof(SetGoal));
            ThrowIfInputAllowedForMode();
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw new InvalidOperationException("Runtime Goal changes are not allowed in StaticBaked mode.");

            if (!request.HasGoal)
            {
                if (_goalTransform == null && !_hasExplicitGoal)
                    return;
                _goalTransform = null;
                _hasExplicitGoal = false;
                _explicitGoalWorld = default;
                MarkGoalDirty();
                return;
            }

            if (_goalTransform == null
                && _hasExplicitGoal
                && FlowFieldGridSpace.Approximately(
                    _explicitGoalWorld,
                    request.WorldPosition,
                    0.00000001d)
                && Mathf.Abs(_flow.GoalInfluenceRadius - request.InfluenceRadius) <= FlowFieldManagerConfig.ValueEpsilon)
                return;

            _goalTransform = null;
            _hasExplicitGoal = true;
            _explicitGoalWorld = request.WorldPosition;
            _flow.GoalInfluenceRadius = request.InfluenceRadius;
            MarkGoalDirty();
        }

        public void NotifyCellsDirty()
        {
            _boundary.ThrowIfInUserCode(nameof(NotifyCellsDirty));
            ThrowIfInputAllowedForMode();
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw new InvalidOperationException("Cell regeneration is not allowed in StaticBaked mode.");
            ActiveMode.MarkCellsDirty();
        }

        internal void MarkConfigurationStale()
        {
            ActiveMode.MarkConfigurationStale();
        }

        public bool RegisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            ThrowIfInputAllowedForMode();
            return ActiveSession.RegisterModifierCommand(modifier);
        }

        public bool UnregisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                return false;
            // Removal needs no valid configuration; a component must be able
            // to detach while the Manager waits for an explicit rebuild or a
            // retry, which would otherwise keep applying it.
            if (!IsFaulted)
                ThrowIfLifecycleUnavailable();
            return ActiveSession.UnregisterModifierCommand(modifier);
        }

        public void NotifyModifierChanged(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierChange change)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            if ((change & ~(FlowFieldModifierChange.Value
                | FlowFieldModifierChange.Area
                | FlowFieldModifierChange.Priority)) != 0)
                throw new ArgumentOutOfRangeException(nameof(change));
            if (change == FlowFieldModifierChange.None)
                return;
            ThrowIfInputAllowedForMode();
            ActiveSession.NotifyModifierChangedCommand(modifier, change);
        }
    }
}
