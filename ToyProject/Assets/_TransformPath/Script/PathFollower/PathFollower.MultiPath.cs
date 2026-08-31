using System;
using UnityEngine;

namespace Common.TransformPath
{
    public partial class PathFollower
    {
        #region Private Methods

        /// <summary>
        /// PathDataConfig를 적용하고 continuous speed 계산을 수행합니다.
        /// 이전 세그먼트의 속도를 유지할 수 있으면 새 세그먼트에도 같은 속도를 적용합니다.
        /// </summary>
        /// <param name="config">적용할 PathDataConfig</param>
        /// <param name="useContinuousSpeed">이전 속도를 유지할지 여부</param>
        private void ApplyPathConfig(MultiPathData.PathDataConfig config, bool useContinuousSpeed)
        {
            float previousSpeed = 0f;
            bool hasPreviousSpeed = false;

            if (useContinuousSpeed)
            {
                if (_moveType == EMoveType.SpeedBased)
                {
                    if (_speed > SPEED_BASED_MIN_SPEED)
                    {
                        previousSpeed = _speed;
                        hasPreviousSpeed = true;
                    }
                }
                else
                {
                    if (_pathData == null || !_pathData.IsInitialized || !_pathData.IsReady)
                        throw new InvalidOperationException("The previous PathData segment is not ready.");
                    float previousPathLength = _pathData.PathLength;
                    if (previousPathLength <= 0f || _duration <= TIME_BASED_MIN_DURATION)
                        throw new InvalidOperationException("Continuous speed requires a measurable previous segment.");
                    previousSpeed = previousPathLength / _duration;
                    hasPreviousSpeed = true;
                }
            }

            _pathData = config.PathData;
            _moveType = config.MoveType;
            _timeCurve = config.TimeCurve;

            if (useContinuousSpeed && hasPreviousSpeed)
            {
                if (_moveType == EMoveType.SpeedBased)
                {
                    if (!IsFinite(previousSpeed)
                        || previousSpeed < SPEED_BASED_MIN_SPEED
                        || previousSpeed > SPEED_BASED_MAX_SPEED)
                        throw new ArgumentOutOfRangeException(nameof(previousSpeed));
                    _speed = previousSpeed;
                }
                else
                {
                    if (config.PathData.PathLength <= 0f || previousSpeed <= 0f)
                        throw new InvalidOperationException("Continuous speed requires a measurable segment.");
                    float targetDuration = config.PathData.PathLength / previousSpeed;
                    if (!IsFinite(targetDuration)
                        || targetDuration < TIME_BASED_MIN_DURATION
                        || targetDuration > TIME_BASED_MAX_DURATION)
                        throw new ArgumentOutOfRangeException(nameof(targetDuration));
                    _duration = targetDuration;
                }
            }
            else
            {
                if (config.MoveType == EMoveType.TimeBased)
                    _duration = config.Value;
                else
                    _speed = config.Value;
            }
        }

        private void StartCurrentPath()
        {
            if (!_useMultiPaths)
            {
                StartSinglePath(_onMultiComplete);
                return;
            }

            if (_multiPathData == null)
                throw new InvalidOperationException("MultiPathData is required for a multi-path move.");
            if (_currentPathIndex < 0 || _currentPathIndex >= _multiPathData.PathCount)
                throw new InvalidOperationException("MultiPathData segment index is invalid.");

            MultiPathData.PathDataConfig config = _multiPathData.PathDataConfigs[_currentPathIndex];

            if (config == null || config.PathData == null)
                throw new InvalidOperationException("MultiPathData contains an invalid segment.");

            bool useContinuous = _useContinuousSpeedOnPathChange && _currentPathIndex > 0;
            ApplyPathConfig(config, useContinuous);

            _loop = false;
            StartSinglePath(OnCurrentPathComplete);

            _onPathChanged?.Invoke(_currentPathIndex);
            PublishSegmentChanged();
        }

        private void OnCurrentPathComplete()
        {
            if (!_useMultiPaths)
            {
                _onComplete?.Invoke();
                return;
            }

            MoveToNextPath();
        }

        private void MoveToNextPath()
        {
            _currentPathIndex++;

            if (_multiPathData == null || _currentPathIndex >= _multiPathData.PathCount)
            {
                CompleteAllPaths();
                return;
            }

            StartCurrentPath();
        }

        private void CompleteAllPaths()
        {

            _isMoving = false;
            _useMultiPaths = false;
            _moveCoroutine = null;
            StopRestoreSpeed();
            UnsubscribeFromActiveProvider();
            PublishState(EPathFollowerState.Stopped);
            PublishCompleted();
            _onMultiComplete?.Invoke();
        }

        private bool IsOnLastMultiPathSegment()
        {
            if (!_useMultiPaths || _multiPathData == null)
                return true;

            return _currentPathIndex >= _multiPathData.PathCount - 1;
        }

        #endregion
    }
}
