using System;
using System.Collections.Generic;
using UnityEngine;


namespace Common.TransformPath
{
    /// <summary>
    /// 경로 이벤트 한 줄 (정규화 시각, 이벤트 SO).
    /// </summary>
    [Serializable]
    public struct PathEventEntry
    {
        public float NormalizedTime;

        public PathEventSettingSO EventSetting;
    }


    /// <summary>
    /// 경로 데이터 클래스
    /// 연출을 위한 이동에 사용
    /// </summary>
    [DefaultExecutionOrder(-300)]
    public partial class PathData : MonoBehaviour, IPathProvider, IPathController, IPathEventSource
    {
        #region Constants

        /// <summary>
        /// 경로 이벤트 배치 상한. 경로 진행도(0~1)와 동일합니다.
        /// 런타임 발화는 <see cref="PathFollower"/>의 인덱스 커서와 completion flush를 사용합니다.
        /// </summary>
        public const float MAX_PATH_EVENT_NORMALIZED_TIME = 0.995f;

        private const int MIN_PATH_POINTS = 2;
        private const int DEFAULT_SEGMENT_COUNT = 500;

        #endregion


        #region Inner Classes / Structs

        public enum ECurveType
        {
            Linear = 0,

            /// <summary>3차 균일 B-스플라인(근사). 내부 웨이포인트는 곡선 위에 없을 수 있습니다.</summary>
            SplineApproximating = 1,

            /// <summary>Catmull-Rom 스플라인(보간). 모든 제어점을 통과합니다.</summary>
            SplineInterpolating = 2,
        }

        public enum ESamplingType
        {
            Uniform,        // 균등 간격 샘플링
            Random,         // 랜덤 위치 샘플링
            DistanceBased   // 거리 기반 샘플링
        }

        #endregion


        #region Path Definition and Cache State

        [SerializeField] private List<Transform> _pathPoints = new List<Transform>();

        [Header("Path events (normalized time → PathEventSettingSO)")]
        [SerializeField] private List<PathEventEntry> _pathEvents = new List<PathEventEntry>();

        [SerializeField] private int _segmentCount = DEFAULT_SEGMENT_COUNT;
        [SerializeField] private int _samplingCount = 10;
        [SerializeField] private ECurveType _curveType = ECurveType.Linear;
        [SerializeField] private ESamplingType _samplingType = ESamplingType.Uniform;

        private Vector3[] _cachedPathPoints = null;
        private float[] _cachedDistances = null;
        private float _cachedPathLength = -1f;
        private bool _isInitialized = false;
        private bool _isFaulted = false;
        private Exception _fault;
        private int _lastPathPointCount = -1;
        private readonly List<Vector3> _validPointsScratch = new List<Vector3>();
        private readonly List<Vector3> _buildPointsScratch = new List<Vector3>();
        private readonly List<float> _segmentDistancesScratch = new List<float>();
        private readonly List<Vector3> _controlPointsScratch = new List<Vector3>();
        private bool _hasConfiguredVectorPoints;

        #endregion

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        private void OnDestroy()
        {
            if (_isInitialized)
                Release();
            else if (_isFaulted)
                Release();
        }


        #region Path Properties

        /// <summary>
        /// 전체 경로 포인트 배열
        /// </summary>
        public Vector3[] PathPoints
        {
            get
            {
                if (!_isInitialized || _isFaulted || _cachedPathLength < 0f)
                    throw new InvalidOperationException("PathData must be initialized before PathPoints is read.");

                return _cachedPathPoints;
            }
        }

        /// <summary>
        /// 전체 경로 길이
        /// </summary>
        public float PathLength
        {
            get
            {
                if (!_isInitialized || _isFaulted || _cachedPathLength < 0f)
                    throw new InvalidOperationException("PathData must be initialized before PathLength is read.");
                return _cachedPathLength;
            }
        }

        /// <summary>
        /// 경로 이벤트 목록 (정규화 시간, <see cref="PathEventSettingSO"/>).
        /// </summary>
        public IReadOnlyList<PathEventEntry> PathEvents
        {
            get
            {
                ThrowIfNotReady();
                return _pathEvents;
            }
        }

        /// <summary>
        /// 등록된 경로 이벤트가 하나 이상인지 여부
        /// </summary>
        public bool HasPathEvents => _pathEvents != null && _pathEvents.Count > 0;

        #endregion


        #region Path Construction and Query API

        /// <summary>
        /// 경로 데이터를 초기화합니다
        /// </summary>
        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("PathData is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("PathData is faulted. Call Release before Init.", _fault);

            try
            {
                if (_hasConfiguredVectorPoints)
                {
                    if (_validPointsScratch.Count < MIN_PATH_POINTS)
                        throw new ArgumentException("PathData requires at least two configured control points.", nameof(_validPointsScratch));
                }
                else if (!CollectValidWorldPoints(_pathPoints))
                {
                    throw new ArgumentException("PathData requires at least two valid control points.", nameof(_pathPoints));
                }

                BuildFromPoints(_validPointsScratch);
            }
            catch (Exception exception)
            {
                if (_fault == null)
                    _fault = exception;
                _isFaulted = true;
                _isInitialized = false;
                throw;
            }
        }

        /// <summary>
        /// 인스펙터에 등록된 제어점 Transform의 월드 좌표를 순서대로 <paramref name="destination"/>에 복사합니다.
        /// 런타임에서 별도 경로 데이터를 만들 때 사용할 제어점을 복사합니다.
        /// </summary>
        /// <param name="destination">복사 대상 리스트</param>
        /// <param name="clearDestination">true이면 복사 전에 리스트를 비웁니다</param>
        /// <returns>항상 true. 제어점이 부족하거나 구조가 유효하지 않으면 예외를 던집니다.</returns>
        public bool CopyWorldControlPoints(List<Vector3> destination, bool clearDestination = true)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            // Copying is a provider query, so fail before mutating the caller's
            // list when this PathData has not completed its Init/Rebuild contract.
            ThrowIfNotReady();

            if (clearDestination)
                destination.Clear();

            if (_pathPoints == null)
                throw new InvalidOperationException("PathData control points are not configured.");

            for (int i = 0; i < _pathPoints.Count; i++)
            {
                Transform point = _pathPoints[i];
                if (point == null)
                    throw new ArgumentNullException($"{nameof(_pathPoints)}[{i}]", "Control point references cannot be null.");
                if (!IsFinite(point.position))
                    throw new ArgumentOutOfRangeException($"{nameof(_pathPoints)}[{i}]", "Control point position must be finite.");
                destination.Add(point.position);
            }

            if (destination.Count < MIN_PATH_POINTS)
                throw new ArgumentException("PathData requires at least two control points.", nameof(destination));

            return true;
        }

        /// <summary>
        /// 외부에서 제공된 Transform 리스트로 경로 데이터를 초기화합니다
        /// </summary>
        /// <param name="pathPoints">경로 포인트 Transform 리스트</param>
        public void ConfigureControlPoints(List<Transform> pathPoints)
        {
            ThrowIfFaulted();
            if (pathPoints == null)
                throw new ArgumentNullException(nameof(pathPoints));
            if (pathPoints.Count < MIN_PATH_POINTS)
                throw new ArgumentException("At least two control points are required.", nameof(pathPoints));

            if (!CollectValidWorldPoints(pathPoints))
                throw new ArgumentException("At least two valid control points are required.", nameof(pathPoints));

            _pathPoints = new List<Transform>(pathPoints);
            _hasConfiguredVectorPoints = false;
            MarkStale();
        }

        /// <summary>
        /// 외부에서 제공된 Vector3 배열로 경로 데이터를 초기화합니다
        /// </summary>
        /// <param name="points">경로 포인트 Vector3 배열</param>
        public void ConfigureControlPoints(Vector3[] points)
        {
            ThrowIfFaulted();
            if (points == null)
                throw new ArgumentNullException(nameof(points));
            if (points.Length < MIN_PATH_POINTS)
                throw new ArgumentException("At least two control points are required.", nameof(points));

            for (int i = 0; i < points.Length; i++)
                if (!IsFinite(points[i]))
                    throw new ArgumentOutOfRangeException(nameof(points));

            _validPointsScratch.Clear();
            for (int i = 0; i < points.Length; i++)
                _validPointsScratch.Add(points[i]);

            _hasConfiguredVectorPoints = true;
            MarkStale();
        }

        /// <summary>
        /// 외부에서 제공된 Vector3 리스트로 경로 데이터를 초기화합니다
        /// </summary>
        /// <param name="points">경로 포인트 Vector3 리스트</param>
        public void ConfigureControlPoints(List<Vector3> points)
        {
            ThrowIfFaulted();
            if (points == null)
                throw new ArgumentNullException(nameof(points));
            if (points.Count < MIN_PATH_POINTS)
                throw new ArgumentException("At least two control points are required.", nameof(points));

            for (int i = 0; i < points.Count; i++)
                if (!IsFinite(points[i]))
                    throw new ArgumentOutOfRangeException(nameof(points));

            _validPointsScratch.Clear();
            _validPointsScratch.AddRange(points);
            _hasConfiguredVectorPoints = true;
            MarkStale();
        }

        /// <summary>
        /// 0~1의 정규화된 값으로 경로 상의 위치를 가져옵니다
        /// </summary>
        /// <param name="normalizedValue">정규화된 진행도 (0~1)</param>
        /// <returns>경로 상의 위치</returns>
        public Vector3 GetPointOnPath(float normalizedValue)
        {
            ThrowIfNotReady();

            if (!IsFinite(normalizedValue) || normalizedValue < 0f || normalizedValue > 1f)
                throw new ArgumentOutOfRangeException(nameof(normalizedValue));

            if (normalizedValue <= 0f)
                return _cachedPathPoints[0];

            if (normalizedValue >= 1f)
                return _cachedPathPoints[_cachedPathPoints.Length - 1];

            float targetDistance = normalizedValue * _cachedPathLength;
            int segmentIndex = PathGeometryUtility.FindSegmentIndex(_cachedDistances, targetDistance);

            if (segmentIndex < 0 || segmentIndex >= _cachedDistances.Length - 1)
                throw new InvalidOperationException("PathData distance cache is inconsistent.");

            float segmentStart = _cachedDistances[segmentIndex];
            float segmentEnd = _cachedDistances[segmentIndex + 1];
            float segmentLength = segmentEnd - segmentStart;

            if (segmentLength <= 0f)
                return _cachedPathPoints[segmentIndex];

            float localT = (targetDistance - segmentStart) / segmentLength;

            if (segmentIndex + 1 < _cachedPathPoints.Length)
                return Vector3.Lerp(_cachedPathPoints[segmentIndex], _cachedPathPoints[segmentIndex + 1], localT);

            return _cachedPathPoints[segmentIndex];
        }

        /// <summary>
        /// 거리 값을 전체 경로 길이에 비례해 0~1로 정규화하여 경로 상의 위치를 가져옵니다
        /// </summary>
        /// <param name="distance">거리 값 (전체 PathLength 기준)</param>
        /// <returns>경로 상의 위치</returns>
        public Vector3 GetPointAtDistance(float distance)
        {
            ThrowIfNotReady();
            if (!IsFinite(distance) || distance < 0f || distance > _cachedPathLength)
                throw new ArgumentOutOfRangeException(nameof(distance));
            if (_cachedPathLength <= 0f)
                throw new InvalidOperationException("PathData has no measurable path length.");

            float normalizedValue = distance / _cachedPathLength;
            return GetPointOnPath(normalizedValue);
        }


        /// <summary>
        /// 경로 이벤트를 <see cref="PathEventEntry.NormalizedTime"/> 오름차순으로 정렬합니다.
        /// </summary>
        /// <returns>리스트 순서가 바뀌었으면 true</returns>
        public bool SortPathEventsByNormalizedTime()
        {
            if (_pathEvents == null || _pathEvents.Count < 2)
                return ClampPathEventNormalizedTimes();

            bool orderChanged = false;

            for (int i = 1; i < _pathEvents.Count; i++)
            {
                if (_pathEvents[i].NormalizedTime < _pathEvents[i - 1].NormalizedTime)
                {
                    _pathEvents.Sort((a, b) => a.NormalizedTime.CompareTo(b.NormalizedTime));
                    orderChanged = true;
                    break;
                }
            }

            if (ClampPathEventNormalizedTimes())
                orderChanged = true;

            return orderChanged;
        }

        /// <summary>
        /// 등록된 경로 이벤트의 <see cref="PathEventEntry.NormalizedTime"/>을 상한 이내로 보정합니다.
        /// </summary>
        /// <returns>한 개 이상의 값이 변경되었으면 true</returns>
        public bool ClampPathEventNormalizedTimes()
        {
            if (_pathEvents == null || _pathEvents.Count == 0)
                return false;

            bool anyChanged = false;

            for (int i = 0; i < _pathEvents.Count; i++)
            {
                PathEventEntry entry = _pathEvents[i];
                float clampedTime = ClampPathEventNormalizedTime(entry.NormalizedTime);

                if (Mathf.Approximately(entry.NormalizedTime, clampedTime))
                    continue;

                entry.NormalizedTime = clampedTime;
                _pathEvents[i] = entry;
                anyChanged = true;
            }

            return anyChanged;
        }

        public static float ClampPathEventNormalizedTime(float normalizedTime)
            => Mathf.Clamp(normalizedTime, 0f, MAX_PATH_EVENT_NORMALIZED_TIME);

        #endregion

        #region Provider Contract State

        private int _revision = 0;

        #endregion


        #region Provider Contract

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;
        public bool IsReady => _isInitialized && !_isFaulted && _cachedPathLength >= 0f
            && _cachedPathPoints != null && _cachedPathPoints.Length > 0;
        public int Revision => _revision;

        public event Action PathChanged;

        #endregion


        #region Provider API

        public Vector3 Sample(float normalizedTime)
        {
            ThrowIfNotReady();
            if (!IsFinite(normalizedTime) || normalizedTime < 0f || normalizedTime > 1f)
                throw new ArgumentOutOfRangeException(nameof(normalizedTime));
            return GetPointOnPath(normalizedTime);
        }

        public Vector3 SampleDistance(float distance)
        {
            ThrowIfNotReady();
            if (!IsFinite(distance) || distance < 0f || distance > _cachedPathLength)
                throw new ArgumentOutOfRangeException(nameof(distance));
            return GetPointAtDistance(distance);
        }

        public void Rebuild()
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathData must be initialized before Rebuild.");
            try
            {
                if (_hasConfiguredVectorPoints)
                {
                    if (_validPointsScratch.Count < MIN_PATH_POINTS)
                        throw new ArgumentException("PathData requires at least two configured control points.", nameof(_validPointsScratch));
                    BuildFromPoints(_validPointsScratch);
                }
                else if (!CollectValidWorldPoints(_pathPoints))
                    throw new ArgumentException("PathData requires at least two valid control points.");
                else
                    BuildFromPoints(_validPointsScratch);
            }
            catch (Exception exception)
            {
                if (_fault == null)
                    _fault = exception;
                _isFaulted = true;
                _isInitialized = false;
                throw;
            }
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("PathData has not been initialized.");
            _isInitialized = false;
            _isFaulted = false;
            _cachedPathPoints = null;
            _cachedDistances = null;
            _cachedPathLength = -1f;
            _fault = null;
        }

        private void ThrowIfNotReady()
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("PathData is not initialized.");
            if (!IsReady)
                throw new InvalidOperationException("PathData is not ready.");
        }

        private void MarkStale()
        {
            if (_isFaulted)
                throw new InvalidOperationException("PathData is faulted. Call Release before changing its configuration.", _fault);
            if (_isInitialized)
                _cachedPathLength = -1f;
        }

        private void ThrowIfFaulted()
        {
            if (_isFaulted)
                throw new InvalidOperationException("PathData is faulted. Call Release before using it.", _fault);
        }

        internal void NotifyPathBuild(bool resultChanged)
        {
            if (!resultChanged)
                return;

            _revision++;
            PathChanged?.Invoke();
        }

        #endregion


        #region Provider Helpers

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value)
            => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        #endregion

        #region Sampling

        private Vector3[] SamplePointsOnPath(int count)
        {
            if (count <= 0)
                return Array.Empty<Vector3>();

            if (_cachedPathPoints == null || _cachedPathPoints.Length == 0)
                throw new InvalidOperationException("PathData must be initialized before sampling points.");

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
                    throw new ArgumentException("Unsupported sampling type.", nameof(_samplingType));
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
