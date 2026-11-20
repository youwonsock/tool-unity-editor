using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Collider bounds lag a Transform move until the physics scene syncs, and
    /// Auto Sync Transforms is off unless a project turns it on. A read that
    /// must be current syncs at once; the components that watch their own
    /// Collider every frame share one sync per frame.
    /// </summary>
    internal static class FlowFieldPhysicsSync
    {
        private static int _syncedFrame = -1;

        // Domain reload may be disabled, so each Play session starts over.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState() => _syncedFrame = -1;

        /// <summary>
        /// Syncs at most once a frame. A move made before the first caller's
        /// LateUpdate is seen this frame; one made by a later LateUpdate is
        /// seen the next frame.
        /// </summary>
        internal static void SyncOncePerFrame()
        {
            if (Physics.autoSyncTransforms)
                return;
            int frame = Time.frameCount;
            if (frame == _syncedFrame)
                return;
            _syncedFrame = frame;
            Physics.SyncTransforms();
        }

        /// <summary>The Collider's bounds after syncing now.</summary>
        internal static Bounds SyncedBounds(Collider collider)
        {
            Physics.SyncTransforms();
            return collider.bounds;
        }
    }
}
