using System;
using System.Collections.Generic;
namespace Common.TransformPath
{
    internal interface IPathRuntimeTickable
    {
        int RuntimeOrder { get; }
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
        private readonly struct FrameEntry
        {
            public readonly IPathRuntimeTickable Item;
            public readonly ulong PlaybackId;
            public readonly ulong RegistrationVersion;

            public FrameEntry(IPathRuntimeTickable item, ulong playbackId, ulong registrationVersion)
            {
                Item = item;
                PlaybackId = playbackId;
                RegistrationVersion = registrationVersion;
            }
        }

        private readonly List<IPathRuntimeTickable> _items = new List<IPathRuntimeTickable>(32);
        private readonly List<FrameEntry> _frameEntries = new List<FrameEntry>(32);
        // Only active registrations are retained. A driver-wide counter keeps
        // remove/re-register distinguishable even when PlaybackId is unchanged.
        private readonly Dictionary<IPathRuntimeTickable, ulong> _registrationVersions =
            new Dictionary<IPathRuntimeTickable, ulong>(32);
        private ulong _nextRegistrationVersion;

        public Exception LastException { get; private set; }

        public void Register(IPathRuntimeTickable item)
        {
            if (item != null && !_registrationVersions.ContainsKey(item))
            {
                int insertIndex = _items.Count;
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].RuntimeOrder > item.RuntimeOrder)
                    {
                        insertIndex = i;
                        break;
                    }
                }
                _items.Insert(insertIndex, item);
                _registrationVersions.Add(item, ++_nextRegistrationVersion);
            }
        }

        public void Unregister(IPathRuntimeTickable item)
        {
            if (item != null && _registrationVersions.Remove(item))
                _items.Remove(item);
        }

        public void Tick(
            float deltaTime,
            float unscaledDeltaTime,
            int frameId)
        {
            _frameEntries.Clear();
            LastException = null;
            for (int i = 0; i < _items.Count; i++)
            {
                IPathRuntimeTickable item = _items[i];
                _frameEntries.Add(new FrameEntry(item, item.PlaybackId, _registrationVersions[item]));
            }

            for (int i = 0; i < _frameEntries.Count; i++)
            {
                FrameEntry frameEntry = _frameEntries[i];
                IPathRuntimeTickable item = frameEntry.Item;
                // An earlier callback may have removed an item from the
                // driver.  The frame snapshot still owns its reference, so
                // check membership before invoking it.
                if (item == null
                    || !_registrationVersions.TryGetValue(item, out ulong registrationVersion)
                    || registrationVersion != frameEntry.RegistrationVersion
                    || item.PlaybackId != frameEntry.PlaybackId)
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
                                frameEntry.PlaybackId,
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
