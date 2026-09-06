using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Presentation adapter for playback and queue values. The core playback
    /// contracts do not depend on Animator or other Unity presentation state.
    /// </summary>
    public sealed class PathFollowerAnimatorView : MonoBehaviour
    {
        [SerializeField] private Animator _animator;
        [SerializeField] private string _speedParamName = "Speed";

        private int _speedParamHash;

        private void Awake()
        {
            _speedParamHash = string.IsNullOrEmpty(_speedParamName)
                ? 0
                : Animator.StringToHash(_speedParamName);
        }

        public void ApplyPlaybackSpeed(float speed)
        {
            if (_animator != null)
                _animator.speed = Mathf.Max(0f, speed);
        }

        public void ApplyQueueMultiplier(float multiplier)
        {
            if (_animator != null && _speedParamHash != 0)
                _animator.SetFloat(_speedParamHash, Mathf.Clamp01(multiplier));
        }
    }
}
