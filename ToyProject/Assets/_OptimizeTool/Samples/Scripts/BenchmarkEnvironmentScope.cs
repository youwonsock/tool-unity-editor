using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Common.OptimizeTool.Samples
{
    /// <summary>Captures measurement overrides and restores each one even if another restoration fails.</summary>
    internal sealed class BenchmarkEnvironmentScope : IDisposable
    {
        private readonly int _vsync = QualitySettings.vSyncCount;
        private readonly int _target = Application.targetFrameRate;
        private readonly int _interval = OnDemandRendering.renderFrameInterval;
        private readonly bool _background = Application.runInBackground;
        private readonly List<(Behaviour behaviour, bool enabled)> _behaviours = new List<(Behaviour, bool)>();
        private bool _disposed;

        public void Apply()
        {
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
            Application.runInBackground = true; OnDemandRendering.renderFrameInterval = 1;
        }
        public void Disable(Behaviour behaviour)
        {
            if (behaviour == null) return;
            _behaviours.Add((behaviour, behaviour.enabled)); behaviour.enabled = false;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Restore(() => QualitySettings.vSyncCount = _vsync);
            Restore(() => Application.targetFrameRate = _target);
            Restore(() => Application.runInBackground = _background);
            Restore(() => OnDemandRendering.renderFrameInterval = _interval);
            for (int i = _behaviours.Count - 1; i >= 0; i--)
            {
                var item = _behaviours[i];
                Restore(() => { if (item.behaviour != null) item.behaviour.enabled = item.enabled; });
            }
            _behaviours.Clear();
        }
        internal static void Restore(Action restore)
        {
            try { restore(); }
            catch (Exception error) { Debug.LogException(error); }
        }
    }
}
