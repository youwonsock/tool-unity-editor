using System;
using UnityEngine;

namespace Common.TransformPath
{
    internal interface IPathTimeScaleStore
    {
        float TimeScale { get; set; }
        float FixedDeltaTime { get; set; }
    }

    internal sealed class UnityPathTimeScaleStore : IPathTimeScaleStore
    {
        public float TimeScale
        {
            get => Time.timeScale;
            set => Time.timeScale = value;
        }

        public float FixedDeltaTime
        {
            get => Time.fixedDeltaTime;
            set => Time.fixedDeltaTime = value;
        }
    }

    /// <summary>
    /// Coordinates the single global Time.timeScale override used by path
    /// events. The newest owner supersedes older owners; stale releases are
    /// ignored.
    /// </summary>
    internal sealed class PathTimeScaleCoordinator
    {
        private struct RequestState
        {
            public bool Active;
            public ulong OwnerId;
            public float BaseScale;
            public float BaseFixedDelta;
            public float StartScale;
            public float Target;
            public float Duration;
            public float Elapsed;
            public AnimationCurve Curve;
            public int StartFrame;
        }

        public static readonly PathTimeScaleCoordinator Shared =
            new PathTimeScaleCoordinator(new UnityPathTimeScaleStore());

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Shared.Reset();
        }

        private RequestState _state;
        private readonly IPathTimeScaleStore _store;
        private int _lastTickFrame = int.MinValue;

        internal PathTimeScaleCoordinator(IPathTimeScaleStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public void Request(
            ulong ownerId,
            float target,
            float duration,
            AnimationCurve curve,
            int startFrame = 0)
        {
            if (ownerId == 0)
                throw new ArgumentOutOfRangeException(nameof(ownerId));
            if (!PathValueUtility.IsInRange(
                    target,
                    0f,
                    PathMovementSettingsUtility.MAX_VALUE))
                throw new ArgumentOutOfRangeException(nameof(target));
            if (!PathValueUtility.IsInRange(
                    duration,
                    PathMovementSettingsUtility.MIN_VALUE,
                    PathMovementSettingsUtility.MAX_VALUE))
                throw new ArgumentOutOfRangeException(nameof(duration));

            float currentScale = _store.TimeScale;
            if (!PathValueUtility.IsFinite(currentScale)
                || !PathValueUtility.IsFinite(_store.FixedDeltaTime))
                throw new InvalidOperationException(
                    "The time scale store returned a non-finite value.");

            if (!_state.Active)
            {
                _state.BaseScale = currentScale;
                _state.BaseFixedDelta = _store.FixedDeltaTime;
            }

            _state.Active = true;
            _state.OwnerId = ownerId;
            // A newer owner takes over from the value currently in effect.
            // The original pair remains the only value restored on release.
            _state.StartScale = currentScale;
            _state.Target = target;
            _state.Duration = duration;
            _state.Elapsed = 0f;
            _state.Curve = curve == null
                ? null
                : new AnimationCurve(curve.keys)
                {
                    preWrapMode = curve.preWrapMode,
                    postWrapMode = curve.postWrapMode,
                };
            _state.StartFrame = startFrame;
            Apply();
        }

        public void Tick(float unscaledDeltaTime, int frame)
        {
            // The coordinator is shared by every event handler. Unity's
            // runtime driver therefore may reach this method more than once
            // for one frame; only the first call may consume that frame's
            // unscaled delta.
            if (frame > 0 && frame == _lastTickFrame)
                return;
            if (frame > 0)
                _lastTickFrame = frame;
            if (!_state.Active
                || (_state.StartFrame > 0 && _state.StartFrame >= frame)
                || unscaledDeltaTime <= 0f)
                return;
            _state.Elapsed += unscaledDeltaTime;
            Apply();
        }

        public void Release(ulong ownerId)
        {
            if (!_state.Active || _state.OwnerId != ownerId)
                return;

            _store.TimeScale = _state.BaseScale;
            _store.FixedDeltaTime = _state.BaseFixedDelta;
            _state = default(RequestState);
            _lastTickFrame = int.MinValue;
        }

        internal void Reset()
        {
            if (_state.Active)
            {
                _store.TimeScale = _state.BaseScale;
                _store.FixedDeltaTime = _state.BaseFixedDelta;
            }
            _state = default(RequestState);
            _lastTickFrame = int.MinValue;
        }

        private void Apply()
        {
            float t = Mathf.Clamp01(_state.Duration <= 0f
                ? 1f
                : _state.Elapsed / _state.Duration);
            if (_state.Curve != null && _state.Curve.length > 0)
                t = Mathf.Clamp01(_state.Curve.Evaluate(t));

            float current = Mathf.Lerp(_state.StartScale, _state.Target, t);
            _store.TimeScale = current;
            float reference = _state.BaseScale > 0f ? _state.BaseScale : 1f;
            _store.FixedDeltaTime = _state.BaseFixedDelta * current / reference;

            if (_state.Elapsed >= _state.Duration)
            {
                _store.TimeScale = _state.Target;
                _store.FixedDeltaTime = _state.BaseFixedDelta * _state.Target / reference;
            }
        }
    }
}
