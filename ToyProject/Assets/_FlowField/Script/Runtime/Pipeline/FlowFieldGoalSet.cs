using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The additional Goals of a session: what each one asks for and the field
    /// built for it. The session changes the requests; a builder's Goal batch
    /// builds the fields on the published scene and agent layers. A field is
    /// due when its request changed, or when any input other than the default
    /// Goal changed since it was built (the base stamp).
    /// </summary>
    internal sealed class FlowFieldGoalSet
    {
        internal sealed class Entry
        {
            internal readonly int Id;
            internal readonly FlowFieldGoalKind Kind;
            internal FlowFieldGoalRequest Request;
            internal int RequestRevision;
            // What the published field was built from; -1 before the first build.
            internal int BuiltRequestRevision = -1;
            internal int BuiltBaseStamp = -1;
            // The field reads answer from, or null while none was built on the
            // current grid and source.
            internal FlowFieldField Field;
            // A world Goal's seeds in this area, or null for a Goal of this
            // Manager alone; the seeds the published field was built with.
            internal FlowFieldGoalSeeds Seeds;
            internal FlowFieldGoalSeeds BuiltSeeds;

            internal Entry(int id, FlowFieldGoalKind kind, in FlowFieldGoalRequest request)
            {
                Id = id;
                Kind = kind;
                Request = request;
            }
        }

        private readonly List<Entry> _entries = new List<Entry>(4);
        // Ids keep counting across Clear, so a handle never names a later Goal.
        // Shared by every session, so a handle never names a Goal of another
        // Manager or of a later session of the same Manager.
        private static int s_nextId = 1;

        internal IReadOnlyList<Entry> Entries => _entries;
        internal int Count => _entries.Count;

        // Advances with every input except a default Goal change.
        internal int BaseStamp { get; private set; }

        // Advances when every field is dropped, so a build that began before
        // publishes nothing.
        internal int FieldEpoch { get; private set; }

        /// <summary>
        /// The base stamp the published field stands for. A default Goal
        /// build sets the stamp it began with when it publishes; with nothing
        /// owed to the default Goal, the published field stands for every
        /// input so far. A Goal built on the published field is built for
        /// this stamp, which may be behind <see cref="BaseStamp"/>.
        /// </summary>
        internal int PublishedStamp { get; private set; }

        // Not yet built for the latest input: the session stays Building.
        internal bool IsDue(Entry entry)
            => entry.BuiltRequestRevision != entry.RequestRevision || entry.BuiltBaseStamp != BaseStamp;

        // Not yet built on the published field: a batch can build it now.
        internal bool IsBuildable(Entry entry)
            => entry.BuiltRequestRevision != entry.RequestRevision || entry.BuiltBaseStamp != PublishedStamp;

        internal bool HasBuildable => HasBuildableOf(FlowFieldGoalKind.Shared) || HasBuildableOf(FlowFieldGoalKind.Group);

        internal bool HasBuildableOf(FlowFieldGoalKind kind)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Kind == kind && IsBuildable(_entries[i]))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The kind the next batch builds. Group Goals move often and wait for
        /// their field, so they go first, but never twice in a row while a
        /// Shared Goal waits.
        /// </summary>
        internal FlowFieldGoalKind NextBatchKind(FlowFieldGoalKind? last)
        {
            bool group = HasBuildableOf(FlowFieldGoalKind.Group);
            bool shared = HasBuildableOf(FlowFieldGoalKind.Shared);
            if (group && shared)
                return last == FlowFieldGoalKind.Group ? FlowFieldGoalKind.Shared : FlowFieldGoalKind.Group;
            return group ? FlowFieldGoalKind.Group : FlowFieldGoalKind.Shared;
        }

        internal void MarkPublished(int stamp) => PublishedStamp = stamp;

        internal bool HasDue
        {
            get
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (IsDue(_entries[i]))
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Whether a Goal's field reads this agent layer. Its arrays then stay
        /// out of the spares, so the next build never writes into them.
        /// </summary>
        internal bool HoldsAgent(FlowFieldAgentLayer agent)
        {
            if (agent == null)
                return false;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (ReferenceEquals(_entries[i].Field?.Agent, agent))
                    return true;
            }
            return false;
        }

        // A positive id is another session's for the same Goal.
        internal Entry Add(FlowFieldGoalKind kind, in FlowFieldGoalRequest request, int id = 0)
        {
            if (id <= 0)
            {
                id = s_nextId;
                s_nextId = s_nextId == int.MaxValue ? 1 : s_nextId + 1;
            }
            var entry = new Entry(id, kind, request);
            _entries.Add(entry);
            return entry;
        }

        internal Entry Find(int id)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Id == id)
                    return _entries[i];
            }
            return null;
        }

        /// <summary>
        /// A new request. A position within 0.0001 world units with the same
        /// radius asks for nothing new. False when the Goal is not in the set.
        /// </summary>
        internal bool TrySetRequest(int id, in FlowFieldGoalRequest request)
        {
            Entry entry = Find(id);
            if (entry == null)
                return false;
            if (FlowFieldGridSpace.Approximately(entry.Request.WorldPosition, request.WorldPosition, 0.00000001d)
                && Mathf.Abs(entry.Request.InfluenceRadius - request.InfluenceRadius) <= FlowFieldManagerConfig.ValueEpsilon)
                return true;
            entry.Request = request;
            unchecked { entry.RequestRevision++; }
            return true;
        }

        /// <summary>
        /// A world Goal's new seeds in this area; seeds that start the same
        /// search ask for nothing new. False when the Goal is not in the set.
        /// </summary>
        internal bool TrySetSeeds(int id, FlowFieldGoalSeeds seeds)
        {
            Entry entry = Find(id);
            if (entry == null)
                return false;
            if (seeds == null ? entry.Seeds == null : seeds.SameSearch(entry.Seeds))
            {
                // The same search under a newer epoch: the field stands for it.
                entry.Seeds = seeds;
                if (entry.BuiltSeeds != null && seeds != null && seeds.SameSearch(entry.BuiltSeeds))
                    entry.BuiltSeeds = seeds;
                return true;
            }
            entry.Seeds = seeds;
            unchecked { entry.RequestRevision++; }
            return true;
        }

        internal bool Remove(int id)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Id != id)
                    continue;
                _entries.RemoveAt(i);
                return true;
            }
            return false;
        }

        internal void TouchBase()
        {
            unchecked { BaseStamp++; }
        }

        // A new grid or source, a stop or a restart: no field stands, and every
        // Goal is built again.
        internal void DropFields()
        {
            unchecked { FieldEpoch++; }
            for (int i = 0; i < _entries.Count; i++)
            {
                _entries[i].Field = null;
                _entries[i].BuiltBaseStamp = -1;
            }
        }

        internal void Clear()
        {
            unchecked { FieldEpoch++; }
            _entries.Clear();
        }
    }
}
