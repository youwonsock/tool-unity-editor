using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// A component the runtime driver ticks. It joins the driver while it is
    /// enabled in Play Mode, so subclasses only implement their own work.
    /// </summary>
    public abstract class PathRuntimeBehaviour : MonoBehaviour, IPathRuntimeTickable
    {
        /// <summary>The playback the driver protects within a frame, or 0.</summary>
        internal virtual ulong TickPlaybackId => 0UL;
        ulong IPathRuntimeTickable.PlaybackId => TickPlaybackId;

        protected virtual void Awake()
        {
            if (Application.isPlaying)
                Initialize();
        }

        protected virtual void OnEnable()
        {
            if (Application.isPlaying)
                PathRuntimeDriverBehaviour.Instance.Register(this);
        }

        protected virtual void OnDisable()
        {
            if (!Application.isPlaying)
                return;
            if (PathRuntimeDriverBehaviour.HasInstance)
                PathRuntimeDriverBehaviour.Instance.Unregister(this);
            OnDisabled();
        }

        protected virtual void OnDestroy()
        {
            if (PathRuntimeDriverBehaviour.HasInstance)
                PathRuntimeDriverBehaviour.Instance.Unregister(this);
            Release();
        }

        /// <summary>Prepares the component. Awake calls it in Play Mode.</summary>
        internal abstract void Initialize();

        /// <summary>Stops what the component runs. OnDestroy calls it.</summary>
        internal abstract void Release();

        protected abstract void OnDisabled();

        void IPathRuntimeTickable.Tick(EPathTickPhase phase, float deltaTime, int frameId) =>
            Tick(phase, deltaTime, frameId);

        void IPathRuntimeTickable.OnTickFault(Exception exception) => OnTickFault(exception);

        internal abstract void Tick(EPathTickPhase phase, float deltaTime, int frameId);

        internal abstract void OnTickFault(Exception exception);
    }
}
