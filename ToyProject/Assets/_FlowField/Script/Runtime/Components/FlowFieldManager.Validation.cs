using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>Checks the serialized settings of each mode before a request uses them.</summary>
    public partial class FlowFieldManager
    {
        private void ValidateVolumeConfiguration(bool validateStaticGoal)
        {
            if (!FlowFieldGridSpace.IsFinite(_cellSize) || _cellSize < FlowFieldBakeBoundsUtility.MinCellSize)
                throw new ArgumentOutOfRangeException(nameof(_cellSize));
            if (!FlowFieldGridSpace.IsFinite(_bakeBoundsLocal.center)
                || !FlowFieldGridSpace.IsFinite(_bakeBoundsLocal.size)
                || _bakeBoundsLocal.size.x <= 0f
                || _bakeBoundsLocal.size.y <= 0f
                || _bakeBoundsLocal.size.z <= 0f)
                throw new ArgumentOutOfRangeException(nameof(_bakeBoundsLocal));
            if (!FlowFieldGridSpace.IsFinite(_defaultFlowDirection)
                || _defaultFlowDirection.sqrMagnitude <= VALUE_EPSILON)
                throw new ArgumentOutOfRangeException(nameof(_defaultFlowDirection));
            if (Quaternion.Angle(transform.rotation, Quaternion.identity) > 0.01f
                || (transform.lossyScale - Vector3.one).sqrMagnitude > 0.0001f)
                throw new ArgumentException("FlowField Manager must use an unrotated transform with unit scale in Volume3D mode.", nameof(transform));
            if (_obstacleLayer.value == 0)
                throw new ArgumentException("Obstacle LayerMask must contain at least one layer.", nameof(_obstacleLayer));
            if (!FlowFieldGridSpace.IsFinite(_obstacleClearance) || _obstacleClearance < 0f)
                throw new ArgumentOutOfRangeException(nameof(_obstacleClearance));
            if (!FlowFieldGridSpace.IsFinite(_goalInfluenceRadius) || _goalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(_goalInfluenceRadius));
            if (!FlowFieldGridSpace.IsFinite(_refreshRate) || _refreshRate < MIN_REFRESH_RATE)
                throw new ArgumentOutOfRangeException(nameof(_refreshRate));
            if (!Enum.IsDefined(typeof(FlowFieldBakeMode), _bakeMode))
                throw new ArgumentOutOfRangeException(nameof(_bakeMode));
            if (_maxGpuWaves < 64)
                throw new ArgumentOutOfRangeException(nameof(_maxGpuWaves), "GPU wave limit must be at least 64.");
            if (!TryGetVolumeLayout(out Bounds volumeBounds, out FlowFieldGridSpace volumeGrid) || !volumeGrid.IsValid)
                throw new ArgumentException("Volume Bounds and Cell Size do not produce a valid grid.", nameof(_bakeBoundsLocal));
            if (volumeGrid.CellCount > FlowFieldBakeBoundsUtility.MaxVolumeCellCount)
                throw new ArgumentOutOfRangeException(nameof(_bakeBoundsLocal), "Volume cell count exceeds one million cells.");
            if (_volumeSession.IsInitialized && BakeMode != _bakeMode)
                throw new InvalidOperationException("FlowField Bake Mode cannot change during an active Init session. Release and Init again.");
            if (_bakeMode == FlowFieldBakeMode.StaticBaked)
            {
                if (_volumeStaticBakeData == null)
                    throw new InvalidOperationException("Volume3D StaticBaked mode requires a FlowFieldVolumeBakeData asset.");
                if (!_volumeStaticBakeData.Matches(
                        volumeGrid,
                        volumeBounds,
                        _obstacleLayer,
                        _obstacleClearance,
                        out string volumeMismatch))
                    throw new InvalidOperationException(volumeMismatch);
                if (validateStaticGoal
                    && !_volumeStaticBakeData.MatchesGoal(
                        HasConfiguredGoal,
                        ConfiguredGoalWorld,
                        _goalInfluenceRadius,
                        out string volumeGoalMismatch))
                    throw new InvalidOperationException(volumeGoalMismatch);
            }
        }

        private void ValidateSurfaceConfiguration(bool validateStaticGoal)
        {
            if (!FlowFieldGridSpace.IsFinite(_cellSize) || _cellSize < FlowFieldBakeBoundsUtility.MinCellSize)
                throw new ArgumentOutOfRangeException(nameof(_cellSize), _cellSize, "Cell Size must be finite and positive.");
            if (!FlowFieldGridSpace.IsFinite(_bakeBoundsLocal.center)
                || !FlowFieldGridSpace.IsFinite(_bakeBoundsLocal.size)
                || _bakeBoundsLocal.size.x <= 0f
                || _bakeBoundsLocal.size.y <= 0f
                || _bakeBoundsLocal.size.z <= 0f)
                throw new ArgumentOutOfRangeException(nameof(_bakeBoundsLocal), "Bake Bounds must be finite and positive.");
            if (!FlowFieldGridSpace.IsFinite(_defaultFlowDirection)
                || _defaultFlowDirection.sqrMagnitude <= VALUE_EPSILON)
                throw new ArgumentOutOfRangeException(nameof(_defaultFlowDirection), "Default Flow Direction must be finite and non-zero.");
            if (Quaternion.Angle(transform.rotation, Quaternion.identity) > 0.01f
                || (transform.lossyScale - Vector3.one).sqrMagnitude > 0.0001f)
                throw new ArgumentException("FlowField Manager must use an unrotated transform with unit scale.", nameof(transform));
            if (_groundBakeLayer.value == 0)
                throw new ArgumentException("Ground Bake LayerMask must contain at least one layer.", nameof(_groundBakeLayer));
            if (_obstacleLayer.value == 0)
                throw new ArgumentException("Obstacle LayerMask must contain at least one layer.", nameof(_obstacleLayer));
            if (!FlowFieldGridSpace.IsFinite(_maxSurfaceSlope) || _maxSurfaceSlope < 0f || _maxSurfaceSlope > 89f)
                throw new ArgumentOutOfRangeException(nameof(_maxSurfaceSlope));
            if (!FlowFieldGridSpace.IsFinite(_maxStepHeight) || _maxStepHeight < 0f)
                throw new ArgumentOutOfRangeException(nameof(_maxStepHeight));
            if (!FlowFieldGridSpace.IsFinite(_obstacleCheckHeight) || _obstacleCheckHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(_obstacleCheckHeight));
            if (!FlowFieldGridSpace.IsFinite(_obstacleCheckCenterOffset))
                throw new ArgumentOutOfRangeException(nameof(_obstacleCheckCenterOffset));
            if (!FlowFieldGridSpace.IsFinite(_obstacleClearance) || _obstacleClearance < 0f)
                throw new ArgumentOutOfRangeException(nameof(_obstacleClearance));
            if (!FlowFieldGridSpace.IsFinite(_refreshRate) || _refreshRate < MIN_REFRESH_RATE)
                throw new ArgumentOutOfRangeException(nameof(_refreshRate));
            if (_maxGpuWaves < 64)
                throw new ArgumentOutOfRangeException(nameof(_maxGpuWaves), "GPU wave limit must be at least 64.");
            if (!Enum.IsDefined(typeof(FlowFieldBakeMode), _bakeMode))
                throw new ArgumentOutOfRangeException(nameof(_bakeMode));
            if (_session.IsInitialized && BakeMode != _bakeMode)
                throw new InvalidOperationException("FlowField Bake Mode cannot change during an active Init session. Release and Init again.");
            if (!FlowFieldGridSpace.IsFinite(_goalInfluenceRadius) || _goalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(_goalInfluenceRadius));

            if (!TryGetBakeLayout(out _, out FlowFieldGridSpace grid) || !grid.IsValid)
                throw new ArgumentException("Bake Bounds and Cell Size do not produce a valid grid.", nameof(_bakeBoundsLocal));

            if (BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                if (_staticBakeData == null)
                    throw new InvalidOperationException("StaticBaked mode requires a FlowFieldStaticBakeData asset.");
                if (!_staticBakeData.MatchesSurface(CreateSurfaceBakeSettings(), out string surfaceMismatch))
                    throw new InvalidOperationException(surfaceMismatch);
                if (!_staticBakeData.MatchesObstacles(
                        _obstacleLayer,
                        _obstacleCheckHeight,
                        _obstacleCheckCenterOffset,
                        _obstacleClearance,
                        out string obstacleMismatch))
                    throw new InvalidOperationException(obstacleMismatch);
                if (validateStaticGoal
                    && !_staticBakeData.MatchesGoal(
                            HasConfiguredGoal,
                            ConfiguredGoalWorld,
                            _goalInfluenceRadius))
                {
                    throw new InvalidOperationException(
                        "Static Flow Bake Goal이 현재 Manager 설정과 다릅니다. ReBake가 필요합니다.");
                }
            }
        }
    }
}
