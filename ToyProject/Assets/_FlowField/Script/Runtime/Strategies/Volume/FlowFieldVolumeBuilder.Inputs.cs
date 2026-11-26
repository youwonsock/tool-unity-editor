using UnityEngine;

namespace Common.FlowField
{
    /// <summary>How Volume3D takes obstacle and Modifier input.</summary>
    internal sealed partial class FlowFieldVolumeBuilder
    {
        // The session marked the uncertain regions; the next build queries them.
        // BakedBase also keeps every region a change reached for a reloaded bake.
        public void ApplyObstacleChange(in FlowFieldObstacleChange change)
        {
            if (_bakeMode == FlowFieldBakeMode.BakedBase && _inputs.Requests.HasLatest)
            {
                float margin = FlowFieldObstacleReach.Margin(_inputs.Requests.Latest.Grid.CellSize, _inputs.Requests.Latest.ObstacleClearance);
                if (change.Previous.HasValue)
                    _inputs.TouchBakedBase(FlowFieldObstacleReach.Expand(change.Previous.Value, margin));
                if (change.Current.HasValue)
                    _inputs.TouchBakedBase(FlowFieldObstacleReach.Expand(change.Current.Value, margin));
            }
            if (_inputs.Requests.Pending)
                return;
            _inputs.Requests.Pending = _inputs.Requests.HasLatest && _inputs.Requests.Latest.Grid.IsValid;
        }

        // Volume3D finds a Modifier's cells per build, so it keeps no area data.
        public IFlowFieldModifierArea CreateModifierArea() => null;

        public void ApplyModifierChange(in FlowFieldModifierUpdate update)
        {
            if (update.Kind == FlowFieldModifierUpdateKind.Changed && update.Requested == FlowFieldModifierChange.None)
                return;
            QueueModifierChange();
        }

        private void QueueModifierChange()
        {
            // Before the first BuildContext exists, interrupted capture owns
            // the retry. Otherwise the latest input must win over the older
            // active or published input, even after pending work was canceled.
            if (!_inputs.Requests.Pending && _fields.Committed == null && _build == null)
                return;
            // Modifiers only affect the final composition. Unobserved colliders
            // wait for the next full build, as in Surface2D. When the running
            // build or, with none, the published field answers the latest
            // request, only the composition is owed, during the first build too.
            if (!_inputs.Requests.Pending && _inputs.Requests.HasLatest)
            {
                FlowFieldVolumeRequest next = _build != null ? _build.Request : _fields.Committed.Request;
                if (next.HasSameResult(_inputs.Requests.Latest))
                {
                    _inputs.ModifierRecomposePending = true;
                    return;
                }
            }
            // The latest input was already accepted, and a Goal move within its
            // cell must not make Submit skip this build.
            if (_inputs.Requests.HasLatest)
                _inputs.Requests.Pending = true;
        }
    }
}
