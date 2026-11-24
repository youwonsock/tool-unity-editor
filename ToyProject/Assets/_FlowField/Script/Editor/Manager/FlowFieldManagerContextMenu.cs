using UnityEditor;

namespace Common.FlowField.Editor
{
    /// <summary>Inspector context menu items of <see cref="FlowFieldManager"/>.</summary>
    internal static class FlowFieldManagerContextMenu
    {
        [MenuItem("CONTEXT/FlowFieldManager/Clear Goal")]
        private static void ClearGoal(MenuCommand command)
            => ((FlowFieldManager)command.context).SetGoal(FlowFieldGoalRequest.None);

        // SetGoal needs a running session that takes runtime Goals.
        [MenuItem("CONTEXT/FlowFieldManager/Clear Goal", true)]
        private static bool CanClearGoal(MenuCommand command)
            => command.context is FlowFieldManager manager
                && manager.IsInitialized
                && !manager.IsFaulted
                && manager.BakeMode != FlowFieldBakeMode.StaticBaked;

        [MenuItem("CONTEXT/FlowFieldManager/Refresh Volume3D Visualization")]
        private static void RefreshVolumeVisualization(MenuCommand command)
        {
            var manager = (FlowFieldManager)command.context;
            if (manager.SpaceMode != FlowFieldSpaceMode.Volume3D)
                return;
            FlowFieldVolumeVisualizationEditor.RequestRefresh(manager);
            SceneView.RepaintAll();
        }
    }
}
