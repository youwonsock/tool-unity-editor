using System;
using System.Collections.Generic;
namespace Common.TransformPath
{
    internal interface IPathRuntimeTickable
    {
        ulong PlaybackId { get; }
        void Tick(float deltaTime, float unscaledDeltaTime, int frameId);
    }

    internal interface IPathRuntimeFaultHandler
    {
        void HandleRuntimeFault(ulong playbackId, Exception exception);
    }

    /// <summary>
    /// Main-thread tick coordinator. It snapshots registered objects and
    /// playback identities before invoking them so a callback-created playback
    /// cannot consume the old execution's frame budget.
    /// </summary>
    internal sealed class PathRuntimeDriver
    {
        private readonly List<IPathRuntimeTickable> _items = new List<IPathRuntimeTickable>(32);
        private readonly List<IPathRuntimeTickable> _frameItems = new List<IPathRuntimeTickable>(32);
        private readonly List<ulong> _framePlaybackIds = new List<ulong>(32);
        private readonly List<int> _frameRegistrationVersions = new List<int>(32);
        private readonly Dictionary<IPathRuntimeTickable, int> _registrationVersions =
            new Dictionary<IPathRuntimeTickable, int>(32);

        public Exception LastException { get; private set; }

        public void Register(IPathRuntimeTickable item)
        {
            if (item != null && !_items.Contains(item))
            {
                _items.Add(item);
                _registrationVersions.TryGetValue(item, out int version);
                _registrationVersions[item] = version + 1;
            }
        }

        public void Unregister(IPathRuntimeTickable item)
        {
            if (item != null)
            {
                _items.Remove(item);
                _registrationVersions.TryGetValue(item, out int version);
                _registrationVersions[item] = version + 1;
            }
        }

        public void Tick(
            float deltaTime,
            float unscaledDeltaTime,
            int frameId)
        {
            _frameItems.Clear();
            _framePlaybackIds.Clear();
            _frameRegistrationVersions.Clear();
            LastException = null;
            for (int i = 0; i < _items.Count; i++)
            {
                IPathRuntimeTickable item = _items[i];
                _frameItems.Add(item);
                _framePlaybackIds.Add(item.PlaybackId);
                _frameRegistrationVersions.Add(_registrationVersions[item]);
            }

            for (int i = 0; i < _frameItems.Count; i++)
            {
                IPathRuntimeTickable item = _frameItems[i];
                // An earlier callback may have removed an item from the
                // driver.  The frame snapshot still owns its reference, so
                // check membership before invoking it.
                if (item == null
                    || !_items.Contains(item)
                    || !_registrationVersions.TryGetValue(item, out int registrationVersion)
                    || registrationVersion != _frameRegistrationVersions[i]
                    || item.PlaybackId != _framePlaybackIds[i])
                    continue;
                try
                {
                    item.Tick(deltaTime, unscaledDeltaTime, frameId);
                }
                catch (Exception exception)
                {
                    if (LastException == null)
                        LastException = exception;
                    if (item is IPathRuntimeFaultHandler faultHandler)
                    {
                        try
                        {
                            faultHandler.HandleRuntimeFault(
                                _framePlaybackIds[i],
                                exception);
                        }
                        catch (Exception)
                        {
                            // Keep the fault that interrupted the runtime
                            // work visible to the Unity bridge. Cleanup is
                            // best-effort and must not hide the original
                            // callback or tick exception.
                        }
                    }
                }
            }
        }

        public void Tick(float deltaTime)
        {
            Tick(deltaTime, deltaTime, 0);
        }
    }
}
