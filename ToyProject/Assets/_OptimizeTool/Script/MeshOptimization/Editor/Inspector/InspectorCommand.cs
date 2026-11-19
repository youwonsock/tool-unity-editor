using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.MeshOptimization.Editor
{
    internal static class InspectorCommand
    {
        /// <summary>Runs the command of an Inspector button and logs a failure against the inspected object.</summary>
        internal static void Run(Object context, Action command)
        {
            try { command(); }
            catch (Exception exception) { Debug.LogException(exception, context); }
        }
    }
}
