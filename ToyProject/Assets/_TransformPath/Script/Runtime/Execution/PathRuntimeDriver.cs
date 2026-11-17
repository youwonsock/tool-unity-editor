using System;
using System.Collections.Generic;

namespace Common.TransformPath
{
    /// <summary>One frame of work, in this order for every registered object.</summary>
    internal enum EPathTickPhase
    {
        /// <summary>Event effects: value blends and delayed events.</summary>
        Effects,
        /// <summary>Queue managers compute each member's limit.</summary>
        Queue,
        /// <summary>Followers move within their limit.</summary>
        Movement,
    }

    internal interface IPathRuntimeTickable
    {
        /// <summary>The playback this object runs, or 0. A new playback waits for the next frame.</summary>
        ulong PlaybackId { get; }
        void Tick(EPathTickPhase phase, float deltaTime, int frameId);
        /// <summary>Called after Tick threw. Cleanup is best-effort.</summary>
        void OnTickFault(Exception exception);
    }

    /// <summary>
    /// Main-thread frame coordinator. It snapshots its objects and their
    /// playbacks before the first phase, so an object registered or restarted
    /// by a callback during the frame starts on the next one.
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

        private static readonly EPathTickPhase[] Phases =
            { EPathTickPhase.Effects, EPathTickPhase.Queue, EPathTickPhase.Movement };

        private readonly List<IPathRuntimeTickable> _items = new List<IPathRuntimeTickable>(32);
        private readonly List<FrameEntry> _frameEntries = new List<FrameEntry>(32);
        // A driver-wide counter keeps remove/re-register distinguishable even
        // when PlaybackId is unchanged.
        private readonly Dictionary<IPathRuntimeTickable, ulong> _registrationVersions =
            new Dictionary<IPathRuntimeTickable, ulong>(32);
        private ulong _nextRegistrationVersion;

        /// <summary>What went wrong in the last Tick. Each failing object still lets the others run.</summary>
        public List<Exception> Faults { get; } = new List<Exception>();

        public void Register(IPathRuntimeTickable item)
        {
            if (item == null || _registrationVersions.ContainsKey(item))
                return;
            _items.Add(item);
            _registrationVersions.Add(item, ++_nextRegistrationVersion);
        }

        public void Unregister(IPathRuntimeTickable item)
        {
            if (item != null && _registrationVersions.Remove(item))
                _items.Remove(item);
        }

        public void Tick(float deltaTime, int frameId)
        {
            _frameEntries.Clear();
            Faults.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                IPathRuntimeTickable item = _items[i];
                _frameEntries.Add(new FrameEntry(item, item.PlaybackId, _registrationVersions[item]));
            }

            for (int phase = 0; phase < Phases.Length; phase++)
            {
                for (int i = 0; i < _frameEntries.Count; i++)
                {
                    FrameEntry entry = _frameEntries[i];
                    IPathRuntimeTickable item = entry.Item;
                    // An earlier callback may have removed or restarted it.
                    if (!_registrationVersions.TryGetValue(item, out ulong version)
                        || version != entry.RegistrationVersion
                        || item.PlaybackId != entry.PlaybackId)
                        continue;
                    try
                    {
                        item.Tick(Phases[phase], deltaTime, frameId);
                    }
                    catch (Exception exception)
                    {
                        Faults.Add(exception);
                        try
                        {
                            item.OnTickFault(exception);
                        }
                        catch (Exception)
                        {
                            // Cleanup must not hide the fault that interrupted the work.
                        }
                    }
                }
            }
        }
    }
}
