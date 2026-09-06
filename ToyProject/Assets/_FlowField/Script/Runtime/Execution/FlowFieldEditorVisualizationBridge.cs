using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Runtime-to-editor seam.  Runtime owns the lifecycle calls but does not
    /// reference UnityEditor or an Editor assembly.  The Editor assembly
    /// installs the handlers during InitializeOnLoad and removes them before
    /// assembly reload/domain teardown.
    /// </summary>
    internal static class FlowFieldEditorVisualizationBridge
    {
        private static Action<FlowFieldManager> _register;
        private static Action<FlowFieldManager> _unregister;
        private static Action<FlowFieldManager> _requestRefresh;
        private static Action<FlowFieldManager> _drawVolume;
        private static Action<FlowFieldManager, Bounds> _drawVolumeBounds;
        private static Action _repaintAll;

        internal static void Install(
            Action<FlowFieldManager> register,
            Action<FlowFieldManager> unregister,
            Action<FlowFieldManager> requestRefresh,
            Action<FlowFieldManager> drawVolume,
            Action<FlowFieldManager, Bounds> drawVolumeBounds,
            Action repaintAll)
        {
            _register = register;
            _unregister = unregister;
            _requestRefresh = requestRefresh;
            _drawVolume = drawVolume;
            _drawVolumeBounds = drawVolumeBounds;
            _repaintAll = repaintAll;
        }

        internal static void Uninstall()
        {
            _register = null;
            _unregister = null;
            _requestRefresh = null;
            _drawVolume = null;
            _drawVolumeBounds = null;
            _repaintAll = null;
        }

        internal static void Register(FlowFieldManager manager)
            => _register?.Invoke(manager);

        internal static void Unregister(FlowFieldManager manager)
            => _unregister?.Invoke(manager);

        internal static void RequestRefresh(FlowFieldManager manager)
            => _requestRefresh?.Invoke(manager);

        internal static void DrawVolume(FlowFieldManager manager)
            => _drawVolume?.Invoke(manager);

        internal static void DrawVolumeBounds(FlowFieldManager manager, Bounds bounds)
            => _drawVolumeBounds?.Invoke(manager, bounds);

        internal static void RepaintAll()
            => _repaintAll?.Invoke();
    }
}
