using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>The Provider and Controller API and the checks every call makes first.</summary>
    public partial class FlowFieldManager
    {
        // The one availability check of the contract: a call needs a session
        // that is initialized (running or stopped) and not faulted.
        private void ThrowIfLifecycleUnavailable()
        {
            if (IsFaulted)
                throw FlowFieldFailures.Faulted(ActiveSession.Fault);
            if (!IsInitialized)
                throw FlowFieldFailures.NotInitialized();
        }

        private void MarkGoalDirty()
            => SubmitConfig(FlowFieldConfigChange.Goal);

        public FlowFieldSample Sample(Vector3 worldPosition)
        {
            ThrowIfLifecycleUnavailable();
            return ActiveSession.Sample(worldPosition);
        }

        public bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            return ActiveSession?.TrySample(worldPosition, out sample) ?? false;
        }

        public FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            ThrowIfLifecycleUnavailable();
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
            ThrowIfLifecycleUnavailable();
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (collider == null)
                throw new InvalidOperationException("Cannot register a destroyed Collider.");
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw FlowFieldFailures.NotInStaticBaked("Dynamic obstacle registration");
            return ActiveSession.RegisterObstacleCommand(collider);
        }

        public bool UnregisterDynamicObstacle(Collider collider)
        {
            // Removal needs no valid configuration, as with Modifiers. A faulted
            // Manager still takes it, or a retry would keep the obstacle.
            if (!IsFaulted)
                ThrowIfLifecycleUnavailable();
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;
            return ActiveSession.UnregisterObstacleCommand(collider);
        }

        /// <summary>
        /// A registered obstacle moved. The cells of its last built bounds and of
        /// its bounds now are rebuilt; a destroyed one stops being registered.
        /// False when the Collider is not registered.
        /// </summary>
        public bool NotifyDynamicObstacleMoved(Collider collider)
        {
            _boundary.ThrowIfInUserCode(nameof(NotifyDynamicObstacleMoved));
            ThrowIfLifecycleUnavailable();
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw FlowFieldFailures.NotInStaticBaked("Obstacle changes");
            return ActiveSession.NotifyObstacleMovedCommand(collider);
        }

        public void NotifyObstacleRegionDirty(Bounds worldBounds)
        {
            _boundary.ThrowIfInUserCode(nameof(NotifyObstacleRegionDirty));
            ThrowIfLifecycleUnavailable();
            if (!FlowFieldGridSpace.IsFinite(worldBounds.center)
                || !FlowFieldGridSpace.IsFinite(worldBounds.size))
                throw new ArgumentOutOfRangeException(nameof(worldBounds));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw FlowFieldFailures.NotInStaticBaked("Obstacle changes");
            ActiveSession.NotifyObstacleRegionDirtyCommand(worldBounds);
        }

        public void SetGoal(in FlowFieldGoalRequest request)
        {
            _boundary.ThrowIfInUserCode(nameof(SetGoal));
            ThrowIfLifecycleUnavailable();
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw FlowFieldFailures.NotInStaticBaked("Runtime Goal changes");

            if (!request.HasGoal)
            {
                if (!_hasExplicitGoal)
                    return;
                _hasExplicitGoal = false;
                _explicitGoalWorld = default;
                MarkGoalDirty();
                return;
            }

            if (_hasExplicitGoal
                && FlowFieldGridSpace.Approximately(
                    _explicitGoalWorld,
                    request.WorldPosition,
                    0.00000001d)
                && Mathf.Abs(_flow.GoalInfluenceRadius - request.InfluenceRadius) <= FlowFieldManagerConfig.ValueEpsilon)
                return;

            _hasExplicitGoal = true;
            _explicitGoalWorld = request.WorldPosition;
            _flow.GoalInfluenceRadius = request.InfluenceRadius;
            MarkGoalDirty();
        }

        public void NotifyCellsDirty()
        {
            _boundary.ThrowIfInUserCode(nameof(NotifyCellsDirty));
            ThrowIfLifecycleUnavailable();
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw FlowFieldFailures.NotInStaticBaked("Cell regeneration");
            SubmitConfig(FlowFieldConfigChange.Cells);
        }

        public bool RegisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            ThrowIfLifecycleUnavailable();
            return ActiveSession.RegisterModifierCommand(modifier);
        }

        public bool UnregisterVectorModifier(IFlowFieldVectorModifier modifier)
        {
            if (modifier == null)
                return false;
            // A component must be able to detach while the Manager is faulted,
            // or a retry would apply it again.
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
            ThrowIfLifecycleUnavailable();
            ActiveSession.NotifyModifierChangedCommand(modifier, change);
        }
    }
}
