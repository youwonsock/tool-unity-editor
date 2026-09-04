using System;
using Common.TransformPath;
using UnityEngine;

namespace Common.TransformPath.Samples
{
    /// <summary>
    /// TransformPath가 전달한 이벤트 메시지를 수신하는 샘플 Receiver입니다.
    /// </summary>
    public sealed class TransformPathSampleMessageReceiver : MonoBehaviour, IPathEventReceiver
    {
        #region Public Methods

        public void ReceivePathEvent(string eventName, IPathFollower follower)
        {
            if (string.IsNullOrEmpty(eventName))
                throw new ArgumentException("Path event name is required.", nameof(eventName));
            if (follower == null)
                throw new ArgumentNullException(nameof(follower));

            Component followerComponent = follower as Component;
            string actorName = followerComponent != null
                ? followerComponent.name
                : follower.GetType().Name;
            Debug.Log(
                $"[TransformPath] Event received: event='{eventName}', actor='{actorName}'",
                this);
        }

        #endregion
    }
}
