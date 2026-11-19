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

    /// <summary>The phases an object takes part in.</summary>
    [Flags]
    internal enum EPathTickPhases
    {
        None = 0,
        Effects = 1 << EPathTickPhase.Effects,
        Queue = 1 << EPathTickPhase.Queue,
        Movement = 1 << EPathTickPhase.Movement,
    }

    internal interface IPathRuntimeTickable
    {
        /// <summary>The phases to tick this object in. Read once, when it registers.</summary>
        EPathTickPhases Phases { get; }
        /// <summary>The playback this object runs, or 0. A new playback waits for the next frame.</summary>
        ulong PlaybackId { get; }
        void Tick(EPathTickPhase phase, float deltaTime, int frameId);
        /// <summary>Called after Tick threw. Cleanup is best-effort; the driver keeps the exception.</summary>
        void OnTickFault();
    }

    /// <summary>
    /// Main-thread frame coordinator. It snapshots its objects and their
    /// playbacks before the first phase, so an object registered or restarted
    /// by a callback during the frame starts on the next one. Each phase keeps
    /// its own list, so a phase only visits the objects that take part in it.
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

        // Registration order within each phase.
        private readonly List<IPathRuntimeTickable>[] _items =
            { new List<IPathRuntimeTickable>(32), new List<IPathRuntimeTickable>(4), new List<IPathRuntimeTickable>(32) };
        private readonly List<FrameEntry>[] _frameEntries =
            { new List<FrameEntry>(32), new List<FrameEntry>(4), new List<FrameEntry>(32) };
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
            EPathTickPhases phases = item.Phases;
            for (int phase = 0; phase < Phases.Length; phase++)
            {
                if ((phases & (EPathTickPhases)(1 << phase)) != 0)
                    _items[phase].Add(item);
            }
            _registrationVersions.Add(item, ++_nextRegistrationVersion);
        }

        public void Unregister(IPathRuntimeTickable item)
        {
            if (item == null || !_registrationVersions.Remove(item))
                return;
            for (int phase = 0; phase < Phases.Length; phase++)
                _items[phase].Remove(item);
        }

        public void Tick(float deltaTime, int frameId)
        {
            Faults.Clear();
            for (int phase = 0; phase < Phases.Length; phase++)
            {
                List<IPathRuntimeTickable> items = _items[phase];
                List<FrameEntry> entries = _frameEntries[phase];
                entries.Clear();
                for (int i = 0; i < items.Count; i++)
                {
                    IPathRuntimeTickable item = items[i];
                    entries.Add(new FrameEntry(item, item.PlaybackId, _registrationVersions[item]));
                }
            }

            for (int phase = 0; phase < Phases.Length; phase++)
            {
                List<FrameEntry> entries = _frameEntries[phase];
                for (int i = 0; i < entries.Count; i++)
                {
                    FrameEntry entry = entries[i];
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
                            item.OnTickFault();
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
