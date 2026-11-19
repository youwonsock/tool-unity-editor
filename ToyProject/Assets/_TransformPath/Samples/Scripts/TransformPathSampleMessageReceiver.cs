using System;
using Common.TransformPath;
using UnityEngine;

namespace Common.TransformPath.Samples
{
    /// <summary>
    /// Sample receiver for the event messages TransformPath delivers.
    /// </summary>
    public sealed class TransformPathSampleMessageReceiver : MonoBehaviour, IPathEventReceiver
    {
        #region Public Methods

        public void OnPathEvent(string eventName, PathFollower follower)
        {
            if (string.IsNullOrEmpty(eventName))
                throw new ArgumentException("Path event name is required.", nameof(eventName));
            if (follower == null)
                throw new ArgumentNullException(nameof(follower));

            Debug.Log(
                $"[TransformPath] Event received: event='{eventName}', actor='{follower.name}'",
                this);
        }

        #endregion
    }
}
