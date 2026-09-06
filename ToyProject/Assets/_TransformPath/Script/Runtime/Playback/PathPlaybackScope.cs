using System;
using System.Collections.Generic;

namespace Common.TransformPath
{
    /// <summary>
    /// Owns resources that belong to one active playback identity. Cached path
    /// data lives on the session; callbacks, effects, and registrations live
    /// here and are discarded together when the identity ends.
    /// </summary>
    internal sealed class PathPlaybackScope : IDisposable
    {
        private readonly List<Action> _cleanup = new List<Action>(4);

        public ulong PlaybackId { get; }
        public bool IsDisposed { get; private set; }

        public PathPlaybackScope(ulong playbackId)
        {
            if (playbackId == 0)
                throw new ArgumentOutOfRangeException(nameof(playbackId));
            PlaybackId = playbackId;
        }

        public void AddCleanup(Action cleanup)
        {
            if (cleanup == null || IsDisposed)
                return;
            _cleanup.Add(cleanup);
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;
            IsDisposed = true;
            for (int i = _cleanup.Count - 1; i >= 0; i--)
            {
                try
                {
                    _cleanup[i]?.Invoke();
                }
                catch
                {
                    // A failed cleanup must not prevent the remaining owners
                    // from being released.
                }
            }
            _cleanup.Clear();
        }
    }
}
