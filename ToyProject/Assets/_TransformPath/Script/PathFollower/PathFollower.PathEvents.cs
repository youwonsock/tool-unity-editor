using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    public partial class PathFollower
    {
        #region Private Methods

        /// <summary>
        /// pause 판매 후 코루틴이 없을 때 <see cref="ResumeMove"/>에서 호출됩니다.
        /// 경로 완료 콜백이 누락된 경우를 보완하며, t=1 end 이벤트는 <see cref="FlushRemainingPathEvents"/>에서 flush됩니다.
        /// </summary>
        private void ForcePathCompletion()
        {
            if (_loop || _normalizedTime < 1f)
                return;

            float travelTracking = 0f;
            if (_moveType == EMoveType.SpeedBased)
                travelTracking = _normalizedTime * GetActivePathLength();

            _isMoving = true;
            HandlePathCompletion(ref travelTracking);
        }

        private void InvokePendingPathEventsUpTo(float normalizedTime)
        {
            if (!_hasPathEvents)
                return;
            if (_pathEventHandler == null)
                throw new InvalidOperationException("PathFollower requires a serialized PathEventHandler.");

            IReadOnlyList<PathEventEntry> pathEvents = GetActivePathEvents();
            if (pathEvents == null)
                throw new InvalidOperationException("Active path provider returned no event list.");
            float upperBound = normalizedTime + PATH_EVENT_TIME_EPSILON;

            while (_nextPathEventIndex < pathEvents.Count)
            {
                int eventIndex = _nextPathEventIndex;
                PathEventEntry entry = pathEvents[eventIndex];
                if (float.IsNaN(entry.NormalizedTime) || float.IsInfinity(entry.NormalizedTime)
                    || entry.NormalizedTime < 0f || entry.NormalizedTime > PathData.MAX_PATH_EVENT_NORMALIZED_TIME)
                    throw new ArgumentOutOfRangeException(nameof(entry.NormalizedTime));
                float eventTime = entry.NormalizedTime;

                if (eventTime > upperBound)
                    break;

                _nextPathEventIndex = eventIndex + 1;

                if (entry.EventSetting == null)
                    throw new ArgumentException("Path events require an EventSetting.", nameof(pathEvents));

                _pathEventHandler.HandleEvent(entry.EventSetting, this);

                // ResetToStart/StartMove 등이 이벤트 인덱스를 되돌리면, 이후 flush는 새 이동 상태가 소유한다.
                if (_nextPathEventIndex <= eventIndex)
                    return;
            }
        }

        /// <summary>
        /// 경로 완료 직전 남은 이벤트(t=1 end 포함)를 flush한 뒤, 필요 시 t=1로 스냅합니다.
        /// <see cref="InvokePathCompletionCallbacks"/>보다 먼저 호출됩니다.
        /// </summary>
        private void FlushRemainingPathEvents()
        {
            if (!_hasPathEvents)
                return;

            InvokePendingPathEventsUpTo(PathData.MAX_PATH_EVENT_NORMALIZED_TIME);

            if (_normalizedTime < 1f)
            {
                _normalizedTime = 1f;
                _previousNormalizedTime = 1f;
                UpdatePosition(1f);
            }
        }

        /// <summary>
        /// 세그먼트 완료 처리. 순서: _isMoving=false → flush → completion callbacks.
        /// flush(<see cref="FlushRemainingPathEvents"/>)는 callbacks보다 먼저 호출되어야 end pause 이벤트가 유효합니다.
        /// </summary>
        private bool HandlePathCompletion(ref float travelTracking)
        {
            if (!_isMoving || _normalizedTime < 1f)
                return false;

            if (_loop)
            {
                _normalizedTime = 0.0f;
                _previousNormalizedTime = 0f;
                travelTracking = 0f;

                if (_hasPathEvents)
                {
                    _nextPathEventIndex = 0;
                    InvokePendingPathEventsUpTo(0f);
                }

                return false;
            }

            _isMoving = false;
            _moveCoroutine = null;

            if (!_useMultiPaths)
            {
                UnsubscribeFromActiveProvider();
                PublishState(EPathFollowerState.Stopped);
                PublishCompleted();
            }

            FlushRemainingPathEvents();
            InvokePathCompletionCallbacks();
            return true;
        }

        private void InvokePathCompletionCallbacks()
        {
            if (_onComplete != null)
            {
                _onComplete.Invoke();
                return;
            }

            if (_onMultiComplete != null && IsOnLastMultiPathSegment())
                CompleteAllPaths();
        }

        #endregion
    }
}
