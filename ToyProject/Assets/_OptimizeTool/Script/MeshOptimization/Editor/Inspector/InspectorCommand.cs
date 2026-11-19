using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.MeshOptimization.Editor
{
    internal static class InspectorCommand
    {
        /// <summary>Play Mode is starting or stopping, when the commands refuse to run.</summary>
        internal static bool PlayModeChanging => EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode;

        /// <summary>Runs the command of an Inspector button and logs a failure against the inspected object.</summary>
        internal static void Run(Object context, Action command)
        {
            try { command(); }
            catch (Exception exception) { Debug.LogException(exception, context); }
        }
    }
}
