using System;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    public partial class PathFollower
    {
        #region Lifecycle Helpers

        private void StartSinglePath(Action onComplete)
        {
            if (_pathData == null)
            {
                Debug.LogWarning("PathFollower: PathData가 설정되지 않았습니다!");
                return;
            }

            _activePathProvider = _pathData;
            SubscribeToActiveProvider();
            RestorePathDataCacheFromSerializedTransformsIfNeeded();

            bool requestReplace = _pendingReplacePathStart;
            _pendingReplacePathStart = false;

            if (requestReplace && _replacePathStartWithFollowerPosition)
            {
                if (_pathData.TryCopyWorldControlPoints(_controlPointScratch))
                {
                    _controlPointScratch[0] = transform.position;
                    PathDataInitialization.Initialize(_pathData, _controlPointScratch, forceReinit: true);
                    _pathDataWithStartOverrideCache = _pathData;
                }
                else
                {
                    Debug.LogWarning("PathFollower: 시작점 치환에 필요한 제어점이 부족합니다. Transform 기준으로 강제 재초기화합니다.");
                    PathDataInitialization.Initialize(_pathData, forceReinit: true);
                }
            }
            else
            {
                PathDataInitialization.Initialize(_pathData);
            }

            AbortMoveCoroutineAndBumpRevision();
            _isMoving = false;
            StopRestoreSpeed();
            int moveRevision = _moveRevision;
            _defaultSpeed = _speed > 0f ? _speed : _pendingDefaultSpeed;
            _defaultDuration = _duration;
            _defaultAnimatorSpeed = _animator != null ? _animator.speed : 1f;
            _pausedAnimatorSpeed = _defaultAnimatorSpeed;
            _isAnimatorPaused = false;
            _hasPathEvents = _pathData.HasPathEvents;
            _onComplete = onComplete;
            _isMoving = true;
            PublishState(EPathFollowerState.Moving);
            ResetMoveProgressState(resetNormalizedTime: true, needsTravelDistanceReset: false);

            if (_hasPathEvents)
            {
                InvokePendingPathEventsUpTo(0f);
                if (moveRevision != _moveRevision)
                    return;
            }

            StartMoveCoroutine(moveRevision);
        }

        private bool IsPathValid()
        {
            if (_activePathProvider != null)
                return _activePathProvider.IsReady;

            if (_pathData == null)
                return false;

            return _pathData.PathPoints != null && _pathData.PathPoints.Length > 0;
        }

        private void RestorePathDataCacheFromSerializedTransformsIfNeeded()
        {
            if (_pathDataWithStartOverrideCache == null)
                return;

            PathDataInitialization.Initialize(_pathDataWithStartOverrideCache, forceReinit: true);
            _pathDataWithStartOverrideCache = null;
        }

        private void ResetMoveProgressState(
            bool resetNormalizedTime,
            bool needsTravelDistanceReset,
            bool needsElapsedTimeReset = false)
        {
            if (resetNormalizedTime)
            {
                _normalizedTime = 0f;
                _previousNormalizedTime = 0f;
                _durationChangeBaseNormalizedTime = 0f;
                _nextPathEventIndex = 0;
            }

            _needsElapsedTimeReset = needsElapsedTimeReset;
            _needsTravelDistanceReset = needsTravelDistanceReset;
        }

        private void CacheRuntimeReferences()
        {
            if (_pathEventHandler == null)
                TryGetComponent(out _pathEventHandler);

            if (_animator == null)
            {
                if (!TryGetComponent(out _animator))
                    _animator = GetComponentInChildren<Animator>();
            }
        }

        #endregion

#if UNITY_EDITOR
        [ContextMenu("Bind Serialized Field")]
        private void BindSerializedField()
        {
            UnityEditor.Undo.RecordObject(this, "Bind Serialized Field");
            TryGetComponent(out _pathEventHandler);
            if (!TryGetComponent(out _animator))
                _animator = GetComponentInChildren<Animator>();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
