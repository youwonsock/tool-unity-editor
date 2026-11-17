using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Immutable sampled path: points, the distance along the path to each one,
    /// and the total length. Sampling interpolates by distance, so a progress of
    /// 0..1 moves at an even speed along the samples.
    /// </summary>
    internal sealed class PathPolyline
    {
        private readonly Vector3[] _points;
        private readonly float[] _distances;

        public float Length { get; }
        public int PointCount => _points.Length;

        public PathPolyline(Vector3[] points, float[] cumulativeDistances)
        {
            _points = points;
            _distances = cumulativeDistances;
            Length = cumulativeDistances[cumulativeDistances.Length - 1];
        }

        public Vector3 GetPoint(int index) => _points[index];

        /// <summary>The sample array itself, for drawing. Never modify it.</summary>
        public Vector3[] Points => _points;

        public Vector3 Sample(float progress)
        {
            if (progress <= 0f)
                return _points[0];
            if (progress >= 1f)
                return _points[_points.Length - 1];
            return SampleDistance(progress * Length);
        }

        public Vector3 SampleDistance(float distance)
        {
            if (distance <= 0f)
                return _points[0];
            if (distance >= Length)
                return _points[_points.Length - 1];

            int index = PathGeometryUtility.FindSegmentStartIndex(_distances, distance);
            float start = _distances[index];
            float length = _distances[index + 1] - start;
            if (length <= Mathf.Epsilon)
                return _points[index];
            return Vector3.Lerp(_points[index], _points[index + 1], (distance - start) / length);
        }

        public bool IsSameAs(PathPolyline other)
        {
            if (other == null
                || other._points.Length != _points.Length
                || !Mathf.Approximately(other.Length, Length))
                return false;
            for (int i = 0; i < _points.Length; i++)
            {
                if (_points[i] != other._points[i])
                    return false;
            }
            return true;
        }
    }
}
