using System;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    public partial class PathData
    {
        #region Sampling

        private Vector3[] SamplePointsOnPath(int count)
        {
            if (count <= 0)
                return Array.Empty<Vector3>();

            if (!_isInitialized)
                Init();

            if (_cachedPathPoints == null || _cachedPathPoints.Length == 0)
            {
                Debug.LogWarning("PathData: 경로 데이터가 비어있습니다!");
                return Array.Empty<Vector3>();
            }

#if UNITY_EDITOR
            if (_samplingType != ESamplingType.Random
                && _cachedSamplePoints != null
                && _cachedSampleCount == count
                && _cachedESamplingType == _samplingType)
                return _cachedSamplePoints;
#endif

            Vector3[] sampledPoints;
            switch (_samplingType)
            {
                case ESamplingType.Uniform:
                    sampledPoints = SampleUniformPoints(count);
                    break;
                case ESamplingType.Random:
                    sampledPoints = SampleRandomPoints(count);
                    break;
                case ESamplingType.DistanceBased:
                    sampledPoints = SampleDistanceBasedPoints(count);
                    break;
                default:
                    sampledPoints = SampleUniformPoints(count);
                    break;
            }

#if UNITY_EDITOR
            if (_samplingType != ESamplingType.Random)
            {
                _cachedSamplePoints = sampledPoints;
                _cachedSampleCount = count;
                _cachedESamplingType = _samplingType;
            }
#endif

            return sampledPoints;
        }

        private Vector3[] SampleUniformPoints(int count)
        {
            Vector3[] results = new Vector3[count];

            for (int i = 0; i < count; i++)
            {
                float t = count > 1 ? (float)i / (count - 1) : 0f;
                results[i] = GetPointOnPath(Mathf.Clamp01(t));
            }

            return results;
        }

        private Vector3[] SampleRandomPoints(int count)
        {
            Vector3[] results = new Vector3[count];

            for (int i = 0; i < count; i++)
                results[i] = GetPointOnPath(UnityEngine.Random.Range(0f, 1f));

            return results;
        }

        private Vector3[] SampleDistanceBasedPoints(int count)
        {
            if (count <= 1)
                return count == 1 ? new[] { GetPointOnPath(0f) } : Array.Empty<Vector3>();

            Vector3[] results = new Vector3[count];
            float pathLength = PathLength;

            if (pathLength <= 0f)
            {
                Vector3 point = GetPointOnPath(0f);
                for (int i = 0; i < count; i++)
                    results[i] = point;
                return results;
            }

            float segmentDistance = pathLength / (count - 1);
            for (int i = 0; i < count; i++)
            {
                float distance = i * segmentDistance;
                results[i] = GetPointOnPath(Mathf.Clamp01(distance / pathLength));
            }

            return results;
        }

        #endregion
    }
}
