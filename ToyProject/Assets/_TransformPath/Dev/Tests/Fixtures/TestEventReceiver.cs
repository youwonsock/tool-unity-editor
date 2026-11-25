using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath.Tests
{
    /// <summary>An event receiver a test can assign to a follower's Event Receiver field.</summary>
    public sealed class TestEventReceiver : MonoBehaviour, IPathEventReceiver
    {
        /// <summary>What heard what, in order; a test adds its own entries to compare against.</summary>
        public readonly List<string> Log = new List<string>();

        public void OnPathEvent(string eventName, PathFollower follower) => Log.Add("receiver " + eventName);
    }
}
