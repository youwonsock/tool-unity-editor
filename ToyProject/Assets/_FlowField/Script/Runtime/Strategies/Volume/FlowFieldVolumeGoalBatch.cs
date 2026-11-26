using System.Collections.Generic;

namespace Common.FlowField
{
    /// <summary>
    /// The Volume3D scratch of one additional Goal: the spare arrays its
    /// builds take, apart from the published field's.
    /// </summary>
    internal sealed class FlowFieldVolumeGoalScratch
    {
        internal readonly FlowFieldSpareArrays Spares = new FlowFieldSpareArrays();
        // A world Goal's portal arrays, sized with the grid.
        internal int[] PortalDirections = System.Array.Empty<int>();
        internal int[] PortalCosts = System.Array.Empty<int>();
        internal float[] PortalHeights = System.Array.Empty<float>();

        internal void ResizePortals(int count)
        {
            if (PortalDirections.Length == count)
                return;
            PortalDirections = new int[count];
            PortalCosts = new int[count];
            PortalHeights = new float[count];
        }
    }

    /// <summary>
    /// One build of the due additional Goals on the published Volume3D field.
    /// Each Goal is a build that keeps the published obstacle results, so its
    /// solve skips the topology and escape jobs and its costs survive as the
    /// Goal's distances. The solves run beside each other on worker threads;
    /// the builder runs every stage.
    /// </summary>
    internal sealed class FlowFieldVolumeGoalBatch : IFlowFieldBuild
    {
        internal sealed class Item
        {
            internal FlowFieldGoalSet.Entry Entry;
            internal int RequestRevision;
            internal FlowFieldVolumeGoalScratch Scratch;
            internal FlowFieldVolumeBuildContext Build;
            internal bool Done;
        }

        private readonly FlowFieldVolumeBuilder _owner;
        private bool _ended;

        // The published field the Goals are built on, and what it stood for.
        internal readonly FlowFieldField<FlowFieldVolumeRequest> Base;
        internal readonly int BaseStamp;
        internal readonly int FieldEpoch;
        internal readonly List<Item> Items = new List<Item>(4);

        internal FlowFieldVolumeGoalBatch(
            FlowFieldVolumeBuilder owner,
            FlowFieldField<FlowFieldVolumeRequest> baseField,
            int baseStamp,
            int fieldEpoch)
        {
            _owner = owner;
            Base = baseField;
            BaseStamp = baseStamp;
            FieldEpoch = fieldEpoch;
        }

        public bool IsLive => !_ended;

        // Nothing can move on until a running solve finishes.
        public bool IsWaitingForWorker
        {
            get
            {
                if (_ended)
                    return false;
                bool waiting = false;
                for (int i = 0; i < Items.Count; i++)
                {
                    Item item = Items[i];
                    if (item.Done)
                        continue;
                    if (!item.Build.IsWaitingForWorker)
                        return false;
                    waiting = true;
                }
                return waiting;
            }
        }

        public bool BuildsAdditionalGoals => true;

        public FlowFieldBuildStep Step(in FlowFieldBuildBudget budget, bool waitForWorker)
            => _owner.StepGoalBatch(this, in budget, waitForWorker);

        public bool Publish() => _owner.PublishGoalBatch(this);

        // Published, rejected or dropped: every solve still running stops.
        internal void End()
        {
            for (int i = 0; i < Items.Count; i++)
                Items[i].Build.End();
            _ended = true;
        }
    }
}
