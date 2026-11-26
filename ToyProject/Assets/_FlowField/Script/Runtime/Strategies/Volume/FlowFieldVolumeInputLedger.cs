using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// What Volume3D input owes the next build: the latest request, a Modifier
    /// recompose, the full obstacle queries an explicit rebuild or a sweep
    /// toggle asked for, and in BakedBase every region a change reached. A
    /// build takes what it builds when it begins; a discarded build gives its
    /// full queries back to the build that replaces it.
    /// </summary>
    internal sealed class FlowFieldVolumeInputLedger
    {
        internal readonly FlowFieldRequestInbox<FlowFieldVolumeRequest> Requests
            = new FlowFieldRequestInbox<FlowFieldVolumeRequest>();
        // Modifier changes reuse the published navigation unless a full build is pending.
        internal bool ModifierRecomposePending;
        // An explicit rebuild re-queries obstacles even when only the Goal changed.
        internal bool ObstacleQueryRequired;
        // The sweep was turned on or off since the last build began.
        internal bool QueryEveryCell;
        // BakedBase: every region an obstacle change reached since Initialize.
        // A reloaded bake queries it again, because the bake still holds what
        // was there before the changes.
        internal Bounds? BakedBaseTouched;

        // A request or a Modifier recompose waits for a build.
        internal bool IsOwed => Requests.Pending || ModifierRecomposePending;

        // Initialize and Release start from nothing.
        internal void Reset()
        {
            Requests.Clear();
            ModifierRecomposePending = false;
            ObstacleQueryRequired = false;
            QueryEveryCell = false;
            BakedBaseTouched = null;
        }

        // A build began with what was owed, or the session cancelled the work.
        internal void ClearOwed()
        {
            Requests.Pending = false;
            ModifierRecomposePending = false;
        }

        internal void TouchBakedBase(Bounds reach)
            => BakedBaseTouched = FlowFieldObstacleReach.Encapsulate(BakedBaseTouched, reach);

        // A full build takes the full queries it was asked for.
        internal void TakeFullQueries(out bool requiresFullQuery, out bool queryEveryCell)
        {
            requiresFullQuery = ObstacleQueryRequired;
            queryEveryCell = QueryEveryCell;
            ObstacleQueryRequired = false;
            QueryEveryCell = false;
        }

        // A discarded build leaves its full queries to the build that replaces it.
        internal void ReturnFullQueries(bool requiresFullQuery, bool queryEveryCell)
        {
            if (requiresFullQuery)
                ObstacleQueryRequired = true;
            if (queryEveryCell)
                QueryEveryCell = true;
        }
    }
}
