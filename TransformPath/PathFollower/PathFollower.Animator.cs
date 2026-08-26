using System.Collections;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    public partial class PathFollower
    {
        #region Public Methods

        /// <summary>
        /// 이동 속도를 기본값으로 즉시 복구합니다
        /// </summary>
        public void RestoreDefaultSpeed()
        {
            StopRestoreSpeed();

            if (_moveType == EMoveType.SpeedBased)
                Speed = _defaultSpeed;
            else
                Duration = _defaultDuration;

            ApplyAnimatorSpeed(_moveType == EMoveType.SpeedBased ? _speed : _duration, _moveType == EMoveType.SpeedBased, true);
        }

        /// <summary>
        /// 이동 속도를 기본값으로 부드럽게 복구합니다
        /// </summary>
        /// <param name="duration">복구에 걸리는 시간</param>
        /// <param name="curve">복구 시 사용할 애니메이션 커브 (null이면 선형 보간)</param>
        public void RestoreDefaultSpeed(float duration, AnimationCurve curve = null)
        {
            StopRestoreSpeed();

            if (duration <= 0f)
            {
                RestoreDefaultSpeed();
                return;
            }

            _restoreSpeedCoroutine = StartCoroutine(Co_RestoreDefaultSpeed(duration, curve));
        }

        /// <summary>
        /// 이동 속도를 새 값으로 즉시 설정합니다
        /// </summary>
        /// <param name="speed">설정할 속도 값 (EMoveType에 따라 해석)</param>
        /// <param name="applyAnimator">Animator 속도도 함께 조정할지 여부</param>
        public void SetSpeed(float speed, bool applyAnimator = true)
        {
            StopRestoreSpeed();

            if (_moveType == EMoveType.SpeedBased)
            {
                Speed = speed;
                ApplyAnimatorSpeed(_speed, true, applyAnimator);
                return;
            }

            float targetDuration = _duration;
            if (_pathData != null && _pathData.PathLength > 0f && speed > 0f)
                targetDuration = _pathData.PathLength / speed;

            Duration = targetDuration;
            ApplyAnimatorSpeed(_duration, false, applyAnimator);
        }

        /// <summary>
        /// 기본 이동 속도 대비 배수를 설정합니다
        /// </summary>
        /// <param name="multiplier">배속 값</param>
        /// <param name="applyAnimator">Animator 속도도 함께 조정할지 여부</param>
        public void SetSpeedMultiplier(float multiplier, bool applyAnimator = true)
        {
            StopRestoreSpeed();

            float clampedMultiplier = Mathf.Max(multiplier, MIN_SPEED_MULTIPLIER);

            if (_moveType == EMoveType.SpeedBased)
            {
                float targetSpeed = _defaultSpeed * clampedMultiplier;
                Speed = targetSpeed;
                ApplyAnimatorSpeed(_speed, true, applyAnimator);
                return;
            }

            float baseDuration = _defaultDuration > 0f ? _defaultDuration : _duration;
            float targetDuration = baseDuration / clampedMultiplier;
            Duration = targetDuration;
            ApplyAnimatorSpeed(_duration, false, applyAnimator);
        }

        /// <summary>
        /// 진행 중인 속도 복구를 중지합니다
        /// </summary>
        public void StopRestoreSpeed()
        {
            if (_restoreSpeedCoroutine != null)
            {
                StopCoroutine(_restoreSpeedCoroutine);
                _restoreSpeedCoroutine = null;
            }
        }

        #endregion


        #region Private Methods

        private void ApplyAnimatorSpeed(float targetValue, bool isSpeedBased, bool applyAnimator)
        {
            if (!applyAnimator)
                return;

            if (_animator == null)
                return;

            _animator.speed = CalculateAnimatorSpeed(targetValue, isSpeedBased);
        }

        private float CalculateAnimatorSpeed(float targetValue, bool isSpeedBased)
        {
            if (_animator == null)
                return 1f;

            if (isSpeedBased)
            {
                if (_defaultSpeed <= 0f)
                    return _defaultAnimatorSpeed;

                return _defaultAnimatorSpeed * (targetValue / _defaultSpeed);
            }

            if (_defaultDuration <= 0f || targetValue <= 0f)
                return _defaultAnimatorSpeed;

            return _defaultAnimatorSpeed * (_defaultDuration / targetValue);
        }

        #endregion


        #region IEnumerator

        private IEnumerator Co_RestoreDefaultSpeed(float duration, AnimationCurve curve)
        {
            bool isSpeedBased = _moveType == EMoveType.SpeedBased;
            float startValue = isSpeedBased ? _speed : _duration;
            float targetValue = isSpeedBased ? _defaultSpeed : _defaultDuration;
            float startAnimatorSpeed = _animator != null ? _animator.speed : _defaultAnimatorSpeed;
            float targetAnimatorSpeed = _animator != null ? CalculateAnimatorSpeed(targetValue, isSpeedBased) : _defaultAnimatorSpeed;

            float timer = 0f;

            while (timer < duration)
            {
                timer += Time.deltaTime;
                float t = Mathf.Clamp01(timer / duration);

                if (curve != null && curve.length > 0)
                    t = curve.Evaluate(t);

                float currentValue = Mathf.Lerp(startValue, targetValue, t);

                if (isSpeedBased)
                    Speed = currentValue;
                else
                    Duration = currentValue;

                if (_animator != null)
                    _animator.speed = Mathf.Lerp(startAnimatorSpeed, targetAnimatorSpeed, t);

                yield return null;
            }

            if (isSpeedBased)
                Speed = targetValue;
            else
                Duration = targetValue;

            if (_animator != null)
                _animator.speed = targetAnimatorSpeed;

            _restoreSpeedCoroutine = null;
        }

        #endregion
    }
}
