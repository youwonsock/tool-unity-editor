using UnityEngine;

namespace Supercent.Common.FlowField
{
    public partial class FlowFieldManager
    {
        public bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!_isReady)
                return false;

            return FlowFieldBilinearSampler.TrySample(
                _context.Grid,
                _surfaceBakeData,
                _context.Workspace,
                worldPosition,
                out sample,
                out _,
                out _,
                out _,
                out _,
                out _);
        }

        public bool TryClampPositionToGrid(
            Vector3 worldPosition,
            out Vector3 clampedPosition,
            out bool clampedX,
            out bool clampedZ)
        {
            clampedPosition = worldPosition;
            clampedX = false;
            clampedZ = false;
            if (!_context.Grid.IsValid || !FlowFieldGoalPipeline.IsFiniteWorldXZ(worldPosition))
                return false;

            clampedPosition = _context.Grid.ClampWorldXZ(worldPosition);
            clampedX = !Mathf.Approximately(worldPosition.x, clampedPosition.x);
            clampedZ = !Mathf.Approximately(worldPosition.z, clampedPosition.z);
            return true;
        }

        public void RegisterDynamicObstacle(Collider collider)
        {
            if (!_obstaclePipeline.RegisterDynamicObstacle(collider))
                return;

            MarkDynamicObstacleRegionDirty(FlowFieldCellRect.FromBounds(_context.Grid, collider.bounds));
        }

        public void UnregisterDynamicObstacle(Collider collider)
        {
            Bounds bounds = collider != null ? collider.bounds : default;
            if (!_obstaclePipeline.UnregisterDynamicObstacle(collider))
                return;

            MarkDynamicObstacleRegionDirty(
                collider != null
                    ? FlowFieldCellRect.FromBounds(_context.Grid, bounds)
                    : FlowFieldCellRect.Invalid);
        }

        public void NotifyObstacleRegionDirty(Bounds worldBounds)
        {
            if (!_context.Grid.IsValid)
            {
                _context.DirtyFlags |= FlowFieldDirtyFlags.DynamicObstacles | FlowFieldDirtyFlags.Escape;
                return;
            }

            MarkDynamicObstacleRegionDirty(FlowFieldCellRect.FromBounds(_context.Grid, worldBounds));
        }

        public void SetGoalPosition(Vector3 worldPosition)
            => SetGoalPosition(worldPosition, 0f);

        public void SetGoalPosition(Vector3 worldPosition, float influenceRadius)
        {
            if (!FlowFieldGoalPipeline.IsFiniteWorldXZ(worldPosition) || !FlowFieldGridSpace.IsFinite(influenceRadius))
            {
                WarnInvalidGoalOnce();
                return;
            }

            float resolvedRadius = Mathf.Max(0f, influenceRadius);
            if (_goalTransform == null
                && _hasExplicitGoal
                && Mathf.Abs(_explicitGoalWorld.x - worldPosition.x) <= VALUE_EPSILON
                && Mathf.Abs(_explicitGoalWorld.z - worldPosition.z) <= VALUE_EPSILON
                && Mathf.Abs(_goalInfluenceRadius - resolvedRadius) <= VALUE_EPSILON)
                return;

            _goalTransform = null;
            _hasExplicitGoal = true;
            _explicitGoalWorld = worldPosition;
            _goalInfluenceRadius = resolvedRadius;
            MarkGoalDirty();
        }

        public void SetGoalTarget(Transform target)
            => SetGoalTarget(target, 0f);

        public void SetGoalTarget(Transform target, float influenceRadius)
        {
            if (target == null)
            {
                ClearGoal();
                return;
            }

            if (!FlowFieldGoalPipeline.IsFiniteWorldXZ(target.position) || !FlowFieldGridSpace.IsFinite(influenceRadius))
            {
                WarnInvalidGoalOnce();
                return;
            }

            float resolvedRadius = Mathf.Max(0f, influenceRadius);
            if (_goalTransform == target
                && !_hasExplicitGoal
                && Mathf.Abs(_goalInfluenceRadius - resolvedRadius) <= VALUE_EPSILON)
                return;

            _goalTransform = target;
            _hasExplicitGoal = false;
            _goalInfluenceRadius = resolvedRadius;
            MarkGoalDirty();
        }

        public void ClearGoal()
        {
            if (_goalTransform == null && !_hasExplicitGoal)
                return;

            _goalTransform = null;
            _hasExplicitGoal = false;
            _invalidGoalWarningIssued = false;
            MarkGoalDirty();
        }
    }
}
