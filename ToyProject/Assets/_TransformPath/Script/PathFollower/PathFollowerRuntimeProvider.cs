using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// PathFollower의 시작점 치환을 위해 원본 PathData를 변경하지 않고 사용하는 런타임 Provider입니다.
    /// </summary>
    internal sealed class PathFollowerRuntimeProvider : IPathProvider, IPathEventSource
    {
        private const int MIN_POINT_COUNT = 2;

        private PathData _source;
        private Vector3[] _points;
        private float[] _distances;
        private float _length;
        private bool _isInitialized;
        private bool _isFaulted;
        private Exception _fault;

        public bool IsInitialized => _isInitialized;
        internal bool IsFaulted => _isFaulted;
        public bool IsReady => _isInitialized && _points != null && _distances != null && _points.Length >= MIN_POINT_COUNT;
        public int Revision
        {
            get
            {
                ThrowIfNotReady();
                return _source.Revision;
            }
        }
        public float PathLength
        {
            get
            {
                ThrowIfNotReady();
                return _length;
            }
        }

        public event Action PathChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<PathEventEntry> PathEvents
        {
            get
            {
                ThrowIfNotReady();
                return _source.PathEvents;
            }
        }

        public void Init(PathData source, Vector3 replacementStart)
        {
            if (_isInitialized)
                throw new InvalidOperationException("Runtime path provider is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("Runtime path provider is faulted; call Release before Init.", _fault);

            try
            {
                if (source == null)
                    throw new ArgumentNullException(nameof(source));
                if (!source.IsInitialized || !source.IsReady)
                    throw new InvalidOperationException("Runtime path provider requires a ready PathData source.");
                if (!IsFinite(replacementStart))
                    throw new ArgumentOutOfRangeException(nameof(replacementStart));

                Vector3[] sourcePoints = source.PathPoints;
                if (sourcePoints == null || sourcePoints.Length < MIN_POINT_COUNT)
                    throw new ArgumentException("PathData source has too few sampled points.", nameof(source));

                _source = source;
                _points = new Vector3[sourcePoints.Length];
                Array.Copy(sourcePoints, _points, sourcePoints.Length);
                _points[0] = replacementStart;
                _distances = PathGeometryUtility.CalculateCumulativeDistances(_points);
                if (_distances.Length != _points.Length)
                    throw new ArgumentException("Runtime path distance cache is inconsistent.", nameof(source));
                _length = _distances[_distances.Length - 1];
                if (!IsFinite(_length) || _length <= 0f)
                    throw new InvalidOperationException("Runtime path provider requires a measurable path.");
                _isInitialized = true;
            }
            catch (Exception exception)
            {
                _isInitialized = false;
                _isFaulted = true;
                if (_fault == null)
                    _fault = exception;
                throw;
            }
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("Runtime path provider has not been initialized.");
            _source = null;
            _points = null;
            _distances = null;
            _length = 0f;
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
        }

        public Vector3 Sample(float normalizedTime)
        {
            ThrowIfNotReady();
            if (!IsFinite(normalizedTime) || normalizedTime < 0f || normalizedTime > 1f)
                throw new ArgumentOutOfRangeException(nameof(normalizedTime));
            return SampleDistance(normalizedTime * _length);
        }

        public Vector3 SampleDistance(float distance)
        {
            ThrowIfNotReady();
            if (!IsFinite(distance) || distance < 0f || distance > _length)
                throw new ArgumentOutOfRangeException(nameof(distance));
            if (distance <= 0f)
                return _points[0];
            if (distance >= _length)
                return _points[_points.Length - 1];

            int segmentIndex = PathGeometryUtility.FindSegmentIndex(_distances, distance);
            if (segmentIndex < 0 || segmentIndex >= _points.Length - 1)
                throw new InvalidOperationException("Runtime path distance cache is inconsistent.");
            float segmentLength = _distances[segmentIndex + 1] - _distances[segmentIndex];
            if (segmentLength <= 0f)
                throw new InvalidOperationException("Runtime path contains a zero-length segment.");
            float localTime = (distance - _distances[segmentIndex]) / segmentLength;
            return Vector3.Lerp(_points[segmentIndex], _points[segmentIndex + 1], localTime);
        }

        private void ThrowIfNotReady()
        {
            if (_isFaulted)
                throw new InvalidOperationException("Runtime path provider is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("Runtime path provider is not initialized.");
            if (!IsReady)
                throw new InvalidOperationException("Runtime path provider is not ready.");
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value)
            => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }
}
