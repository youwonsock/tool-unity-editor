using System;
using UnityEngine;

namespace Common.OptimizeTool.Samples
{
    /// <summary>Restores workload selection and the exact camera pose after success, cancellation or disable.</summary>
    internal sealed class BenchmarkWorkloadScope : IDisposable
    {
        private readonly OptimizeToolScaleWorkload _scale;
        private readonly OptimizeToolOverviewController _overview;
        private readonly int _case, _mode;
        private readonly Camera _camera;
        private readonly Vector3 _position;
        private readonly Quaternion _rotation;
        private readonly bool _orthographic;
        private readonly float _size, _fov;
        private bool _disposed;

        public BenchmarkWorkloadScope(OptimizeToolScaleWorkload scale, OptimizeToolOverviewController overview)
        {
            _scale = scale; _overview = overview;
            _case = scale != null ? scale.ActiveCase : 0;
            _mode = scale != null ? scale.ActiveMode : overview.ActiveIndex;
            _camera = scale != null ? scale.Camera : Camera.main;
            if (_camera == null) throw new InvalidOperationException("The benchmark workload needs a camera.");
            _position = _camera.transform.position; _rotation = _camera.transform.rotation;
            _orthographic = _camera.orthographic; _size = _camera.orthographicSize; _fov = _camera.fieldOfView;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            BenchmarkEnvironmentScope.Restore(() =>
            {
                if (_scale != null) { _scale.SelectCase(_case); _scale.SelectMode(_mode); }
                else if (_overview != null && _overview.IsInitialized) _overview.SelectFeature(_mode);
            });
            BenchmarkEnvironmentScope.Restore(() =>
            {
                if (_camera == null) return;
                if (_camera.TryGetComponent(out OptimizeToolFreeCamera free)) free.SetPose(_position, _rotation);
                else _camera.transform.SetPositionAndRotation(_position, _rotation);
                _camera.orthographic = _orthographic; _camera.orthographicSize = _size; _camera.fieldOfView = _fov;
            });
        }
    }
}
