using System;
using UnityEngine;

namespace Common.TransformPath.Samples
{
    /// <summary>
    /// The queue lane: starts its members spread along the route, restarts each
    /// one when it completes, and pauses or resumes them as the lane is hidden
    /// or shown.
    /// </summary>
    internal sealed class TransformPathQueueLane
    {
        private readonly PathQueueMember[] _members;
        private readonly bool[] _wasMoving;
        private readonly Action[] _restarts;
        private readonly Func<bool> _canRestart;
        private bool _subscribed;

        public TransformPathQueueLane(QueuedPathManager manager, MultiPathData path, PathQueueMember[] members, Func<bool> canRestart)
        {
            Manager = manager;
            Path = path;
            _members = members;
            _wasMoving = new bool[members.Length];
            _restarts = new Action[members.Length];
            _canRestart = canRestart;
        }

        public QueuedPathManager Manager { get; }
        public MultiPathData Path { get; }
        public int MemberCount => _members.Length;
        public PathQueueMember Leader => Manager == null || Manager.MemberCount == 0 ? null : Manager.GetMember(0);

        /// <summary>Starts every idle member, then spreads the moving ones out from the start.</summary>
        public void Start()
        {
            for (int i = 0; i < _members.Length; i++)
            {
                PathQueueMember member = _members[i];
                if (member == null || !member.isActiveAndEnabled || member.Follower.IsMoving)
                    continue;
                member.StartQueuedPlayback(PathPlaybackRequest.Sequence(Path, loop: false));
            }

            if (Path == null || !Path.IsReady)
                return;
            float normalizedGap = Mathf.Clamp((Manager.DefaultSpacing * 1.5f) / Path.PathLength, 0.01f, 0.3f);
            for (int i = 0; i < _members.Length; i++)
            {
                PathQueueMember member = _members[i];
                if (member != null && member.Follower.IsMoving)
                    member.Follower.Seek(Mathf.Clamp01((_members.Length - 1 - i) * normalizedGap));
            }
        }

        public void StopAll()
        {
            for (int i = 0; i < _members.Length; i++)
                _members[i]?.Follower.Stop();
        }

        /// <summary>Pauses every member and remembers which ones were moving.</summary>
        public void PauseAndRemember()
        {
            for (int i = 0; i < _members.Length; i++)
            {
                _wasMoving[i] = _members[i] != null && _members[i].Follower.IsMoving;
                _members[i]?.Follower.Pause();
            }
        }

        /// <summary>Resumes the members that were moving when paused.</summary>
        public void ResumeRemembered()
        {
            for (int i = 0; i < _members.Length; i++)
            {
                if (_members[i] != null && _wasMoving[i])
                    _members[i].Follower.Resume();
                _wasMoving[i] = false;
            }
        }

        public void ForgetRemembered() => Array.Clear(_wasMoving, 0, _wasMoving.Length);

        public void SubscribeRestarts()
        {
            if (_subscribed)
                return;
            for (int i = 0; i < _members.Length; i++)
            {
                PathQueueMember member = _members[i];
                if (member == null)
                    continue;
                _restarts[i] = () => Restart(member);
                member.Follower.Completed += _restarts[i];
            }
            _subscribed = true;
        }

        public void UnsubscribeRestarts()
        {
            if (!_subscribed)
                return;
            for (int i = 0; i < _members.Length; i++)
            {
                if (_members[i] != null && _restarts[i] != null)
                    _members[i].Follower.Completed -= _restarts[i];
            }
            _subscribed = false;
        }

        private void Restart(PathQueueMember member)
        {
            if (!_canRestart() || member == null || !member.isActiveAndEnabled)
                return;
            member.StartQueuedPlayback(PathPlaybackRequest.Sequence(Path, loop: false));
        }
    }
}
