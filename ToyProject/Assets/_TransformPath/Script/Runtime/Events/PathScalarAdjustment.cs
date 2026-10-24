using UnityEngine;

namespace Common.TransformPath
{
    internal enum EPathMoveControlChannel { None, Speed, Duration }

    /// <summary>Game-time movement interpolation bound to one playback identity.</summary>
    internal sealed class PathScalarAdjustment
    {
        private struct ScalarAdjustment
        {
            public bool Active;
            public IPathFollower Follower;
            public float StartValue;
            public float TargetValue;
            public float Duration;
            public float Elapsed;
            public AnimationCurve Curve;
            public int StartFrame;
            public ulong PlaybackId;
            public EPathMoveControlChannel Channel;
        }

        private ScalarAdjustment _scalarAdjustment;
        public bool Active => _scalarAdjustment.Active;

        public void Cancel() => _scalarAdjustment = default(ScalarAdjustment);

        public void Start(
            IPathFollower follower,
            EPathMoveControlChannel channel,
            float target,
            float duration,
            AnimationCurve curve,
            int startFrame)
        {
            if (duration <= 0f)
            {
                if (channel == EPathMoveControlChannel.Speed)
                    follower.SetSpeed(target);
                else
                    follower.SetDuration(target);
                return;
            }

            _scalarAdjustment = new ScalarAdjustment
            {
                Active = true,
                Follower = follower,
                StartValue = channel == EPathMoveControlChannel.Speed ? follower.Speed : follower.Duration,
                TargetValue = target,
                Duration = duration,
                Curve = curve,
                StartFrame = startFrame,
                PlaybackId = follower.PlaybackId,
                Channel = channel,
            };
        }

        public void Tick(float deltaTime, int frame)
        {
            if (!_scalarAdjustment.Active
                || _scalarAdjustment.Follower == null
                || _scalarAdjustment.StartFrame >= frame
                || deltaTime <= 0f)
                return;
            if (_scalarAdjustment.Follower.PlaybackId != _scalarAdjustment.PlaybackId)
            {
                _scalarAdjustment = default(ScalarAdjustment);
                return;
            }

            _scalarAdjustment.Elapsed += deltaTime;
            float t = EvaluateAdjustment(
                _scalarAdjustment.Elapsed,
                _scalarAdjustment.Duration,
                _scalarAdjustment.Curve);
            float value = Mathf.Lerp(_scalarAdjustment.StartValue, _scalarAdjustment.TargetValue, t);
            PathCommandReceipt receipt = _scalarAdjustment.Channel == EPathMoveControlChannel.Speed
                ? _scalarAdjustment.Follower.SetSpeed(value)
                : _scalarAdjustment.Follower.SetDuration(value);
            if (receipt.PlaybackId != _scalarAdjustment.PlaybackId
                || _scalarAdjustment.Follower.PlaybackId != _scalarAdjustment.PlaybackId
                || receipt.StateRevision != _scalarAdjustment.Follower.StateRevision)
            {
                _scalarAdjustment = default(ScalarAdjustment);
                return;
            }

            if (_scalarAdjustment.Elapsed >= _scalarAdjustment.Duration)
            {
                if (_scalarAdjustment.Channel == EPathMoveControlChannel.Speed)
                    _scalarAdjustment.Follower.SetSpeed(_scalarAdjustment.TargetValue);
                else
                    _scalarAdjustment.Follower.SetDuration(_scalarAdjustment.TargetValue);
                _scalarAdjustment = default(ScalarAdjustment);
            }
        }

        private static float EvaluateAdjustment(float elapsed, float duration, AnimationCurve curve)
        {
            if (duration <= 0f)
                return 1f;
            return Mathf.Clamp01(curve == null
                ? elapsed / duration
                : curve.Evaluate(Mathf.Clamp01(elapsed / duration)));
        }

    }
}
