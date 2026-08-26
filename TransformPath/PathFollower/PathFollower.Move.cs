using System.Collections;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    public partial class PathFollower
    {
        #region Private Methods

        private void StartMoveCoroutine(int moveRevision)
        {
            int coroutineId = ++_activeMoveCoroutineId;
            _moveCoroutine = StartCoroutine(Co_MoveWrapper(moveRevision, coroutineId));
        }

        private void UpdatePosition(float normalizedTime)
        {
            if (_activePathProvider != null)
            {
                if (_activePathProvider.TrySample(normalizedTime, out Vector3 providerPosition))
                    transform.position = providerPosition;
                return;
            }

            if (_pathData == null)
                return;

            Vector3 targetPosition = _pathData.GetPointOnPath(normalizedTime);
            transform.position = targetPosition;
        }

        private void AbortMoveCoroutineAndBumpRevision()
        {
            StopMoveCoroutineIfRunning();
            _moveRevision++;
        }

        private bool ShouldAbortMove(int moveRevision) => moveRevision != _moveRevision;

        private void StopMoveCoroutineIfRunning()
        {
            if (_moveCoroutine == null)
                return;

            StopCoroutine(_moveCoroutine);
            _moveCoroutine = null;
        }

        private void UpdatePositionAndEvents(float normalizedTime)
        {
            UpdatePosition(normalizedTime);
            if (_hasPathEvents)
                InvokePendingPathEventsUpTo(normalizedTime);
        }

        #endregion


        #region IEnumerator

        private IEnumerator Co_MoveWrapper(int moveRevision, int coroutineId)
        {
            if (_moveType == EMoveType.TimeBased)
                yield return Co_MoveTimeBased(moveRevision);
            else
                yield return Co_MoveSpeedBased(moveRevision);

            if (coroutineId == _activeMoveCoroutineId)
                _moveCoroutine = null;
        }

        private IEnumerator Co_MoveTimeBased(int moveRevision)
        {
            if (_duration < TIME_BASED_MIN_DURATION)
            {
                Debug.LogWarning("PathFollower: Duration이 너무 작습니다!");
                _isMoving = false;
                RestorePathDataCacheFromSerializedTransformsIfNeeded();
                yield break;
            }

            float elapsedTime = 0f;
            float baseNormalizedTime = _normalizedTime;
            _needsElapsedTimeReset = false;

            while (true)
            {
                if (ShouldAbortMove(moveRevision))
                    yield break;

                if (!_isMoving)
                {
                    yield return null;
                    continue;
                }

                if (!ConsumeProviderChange())
                    yield break;

                if (_needsElapsedTimeReset)
                {
                    _needsElapsedTimeReset = false;
                    baseNormalizedTime = _durationChangeBaseNormalizedTime;
                    elapsedTime = 0f;
                }

                elapsedTime += Time.deltaTime;
                _previousNormalizedTime = _normalizedTime;

                float remainingPath = 1f - baseNormalizedTime;
                float t = Mathf.Clamp01(elapsedTime / _duration);
                float curvedT = _timeCurve != null ? _timeCurve.Evaluate(t) : t;
                _normalizedTime = baseNormalizedTime + curvedT * remainingPath;

                UpdatePositionAndEvents(_normalizedTime);

                if (ShouldAbortMove(moveRevision))
                    yield break;

                float timeTracking = elapsedTime;
                if (TryHandlePathCompletion(ref timeTracking))
                    yield break;
                elapsedTime = timeTracking;
                baseNormalizedTime = _loop && _normalizedTime == 0f ? 0f : baseNormalizedTime;

                yield return null;
            }
        }

        private IEnumerator Co_MoveSpeedBased(int moveRevision)
        {
            if (!IsPathValid())
            {
                Debug.LogWarning("PathFollower: PathData가 유효하지 않습니다!");
                _isMoving = false;
                RestorePathDataCacheFromSerializedTransformsIfNeeded();
                yield break;
            }

            float pathLength = GetActivePathLength();

            if (pathLength <= 0f)
            {
                Debug.LogWarning("PathFollower: PathLength가 0 이하입니다!");
                _isMoving = false;
                RestorePathDataCacheFromSerializedTransformsIfNeeded();
                yield break;
            }

            float traveledDistance = _normalizedTime * pathLength;

            while (true)
            {
                if (ShouldAbortMove(moveRevision))
                    yield break;

                if (!_isMoving)
                {
                    yield return null;
                    continue;
                }

                if (!ConsumeProviderChange())
                    yield break;

                if (_needsTravelDistanceReset)
                {
                    _needsTravelDistanceReset = false;
                    traveledDistance = _normalizedTime * pathLength;
                }

                traveledDistance += _speed * Time.deltaTime;
                _previousNormalizedTime = _normalizedTime;
                _normalizedTime = Mathf.Clamp01(traveledDistance / pathLength);

                UpdatePositionAndEvents(_normalizedTime);

                if (ShouldAbortMove(moveRevision))
                    yield break;

                if (TryHandlePathCompletion(ref traveledDistance))
                    yield break;

                yield return null;
            }
        }

        #endregion
    }
}
