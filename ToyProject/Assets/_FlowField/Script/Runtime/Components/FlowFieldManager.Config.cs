using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The serialized settings as a <see cref="FlowFieldManagerConfig"/>. The
    /// session and the Editor read them only through this snapshot; the mode
    /// builders own the checks and the requests.
    /// </summary>
    public partial class FlowFieldManager
    {
        // The Goal a build uses: SetGoal's, or before Init reads it the Goal
        // Transform, which the Editor bakes.
        private bool HasConfiguredGoal
            => _hasExplicitGoal || _goalTransform != null && !IsInitialized && !IsFaulted;

        private Vector3 ConfiguredGoalWorld
            => _hasExplicitGoal || _goalTransform == null ? _explicitGoalWorld : _goalTransform.position;

        /// <summary>The serialized settings now, with the serialized Bake Mode.</summary>
        internal FlowFieldManagerConfig CaptureConfig() => CaptureConfig(_bakeMode);

        /// <summary>The serialized settings now, with the given Bake Mode.</summary>
        internal FlowFieldManagerConfig CaptureConfig(FlowFieldBakeMode bakeMode)
            => new FlowFieldManagerConfig(
                _spaceMode,
                bakeMode,
                in _grid,
                in _surface,
                in _obstacles,
                in _flow,
                in _bakeAssets,
                transform,
                HasConfiguredGoal,
                ConfiguredGoalWorld);

        private void ValidateConfiguration(bool validateStaticGoal = true)
        {
            FlowFieldManagerConfig config = CaptureConfig();
            bool spaceModeLocked = IsInitialized && SpaceMode != _spaceMode;
            bool bakeModeLocked = IsInitialized && BakeMode != _bakeMode;
            FlowFieldSpaceStrategy.For(_spaceMode).Validate(in config, spaceModeLocked, bakeModeLocked, validateStaticGoal);
        }
    }
}
