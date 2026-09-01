using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// 여러 PathData를 하나의 경로처럼 관리하고 사용할 수 있게 해주는 클래스
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public partial class MultiPathData : MonoBehaviour, IPathSequenceProvider, IPathController
    {
        #region Inner Classes / Structs

        [Serializable]
        public class PathDataConfig
        {
            public PathData PathData;
            public PathFollower.EMoveType MoveType = PathFollower.EMoveType.TimeBased;
            public float Value = 1f;
            public AnimationCurve TimeCurve = AnimationCurve.Linear(0, 0, 1, 1);
        }

        #endregion


        #region Path Sequence State

        [SerializeField] private List<PathDataConfig> _pathDataConfigs = new List<PathDataConfig>();
        
        private float[] _pathLengths;
        private float[] _cumulativePathLengths;
        private float _totalPathLength = -1f;
        private bool _isInitialized = false;
        private bool _isFaulted = false;
        private Exception _fault;

        private readonly List<PathDataConfig> _validConfigsScratch = new List<PathDataConfig>();
        private readonly List<PathData> _validPathsScratch = new List<PathData>();

        #endregion


        #region Path Sequence Properties

        /// <summary>
        /// 관리 중인 PathDataConfig 리스트
        /// </summary>
        public IReadOnlyList<PathDataConfig> PathDataConfigs
        {
            get
            {
                ThrowIfStale();
                return _pathDataConfigs;
            }
        }

        /// <summary>
        /// 전체 경로의 총 길이
        /// </summary>
        public float PathLength
        {
            get
            {
                ThrowIfStale();
                return _totalPathLength;
            }
        }

        /// <summary>
        /// 관리 중인 PathData 개수
        /// </summary>
        public int PathCount
        {
            get
            {
                ThrowIfStale();
                return _pathDataConfigs.Count;
            }
        }

        #endregion


        #region Unity Events

        private void OnValidate()
        {
            // Runtime callers use ConfigureSegments/Rebuild explicitly. Unity can
            // invoke OnValidate while Play Mode is running after serialized data
            // changes elsewhere in the showcase, which would otherwise leave a
            // freshly built sequence stale on the next frame.
            if (!Application.isPlaying && _pathDataConfigs != null)
                _sequenceDirty = true;
        }

        #endregion


        #region Path Sequence API

        /// <summary>
        /// MultiPathData를 초기화합니다
        /// </summary>
        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("MultiPathData is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("MultiPathData is faulted. Call Release before Init.", _fault);

            try
            {
                if (_pathDataConfigs == null || _pathDataConfigs.Count == 0)
                    throw new ArgumentException("MultiPathData requires at least one PathDataConfig.", nameof(_pathDataConfigs));

                BuildFromConfigs(_pathDataConfigs);
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
        /// 외부에서 제공된 PathDataConfig 리스트로 초기화합니다
        /// </summary>
        /// <param name="pathDataConfigs">PathDataConfig 리스트</param>
        public void ConfigureSegments(List<PathDataConfig> pathDataConfigs)
        {
            ThrowIfFaulted();
            if (pathDataConfigs == null)
                throw new ArgumentNullException(nameof(pathDataConfigs));
            if (pathDataConfigs.Count == 0)
                throw new ArgumentException("At least one PathDataConfig is required.", nameof(pathDataConfigs));

            _pathDataConfigs = new List<PathDataConfig>(pathDataConfigs);
            MarkSequenceDirty();
        }

        /// <summary>
        /// 외부에서 제공된 PathData 리스트로 초기화합니다 (기본 설정 사용)
        /// </summary>
        /// <param name="pathDataList">PathData 리스트</param>
        public void ConfigureSegments(List<PathData> pathDataList)
        {
            ThrowIfFaulted();
            if (pathDataList == null)
                throw new ArgumentNullException(nameof(pathDataList));
            if (pathDataList.Count == 0)
                throw new ArgumentException("At least one PathData is required.", nameof(pathDataList));

            List<PathDataConfig> configs = new List<PathDataConfig>();
            foreach (PathData pathData in pathDataList)
            {
                if (pathData == null)
                    throw new ArgumentException("PathData list cannot contain null entries.", nameof(pathDataList));

                PathDataConfig config = new PathDataConfig
                {
                    PathData = pathData,
                    MoveType = PathFollower.EMoveType.TimeBased,
                    Value = 1f,
                    TimeCurve = AnimationCurve.Linear(0, 0, 1, 1)
                };
                configs.Add(config);
            }

            if (configs.Count == 0)
                throw new ArgumentException("At least one valid PathData is required.", nameof(pathDataList));
            _pathDataConfigs = configs;
            MarkSequenceDirty();
        }

        /// <summary>
        /// 외부에서 제공된 PathData 배열로 초기화합니다 (기본 설정 사용)
        /// </summary>
        /// <param name="pathDataArray">PathData 배열</param>
        public void ConfigureSegments(PathData[] pathDataArray)
        {
            ThrowIfFaulted();
            if (pathDataArray == null)
                throw new ArgumentNullException(nameof(pathDataArray));
            if (pathDataArray.Length == 0)
                throw new ArgumentException("At least one PathData is required.", nameof(pathDataArray));

            List<PathData> pathDataList = new List<PathData>(pathDataArray);
            ConfigureSegments(pathDataList);
        }

        /// <summary>
        /// 0~1의 정규화된 값으로 전체 경로 상의 위치를 가져옵니다
        /// </summary>
        /// <param name="normalizedValue">정규화된 진행도 (0~1)</param>
        /// <returns>경로 상의 위치</returns>
        public Vector3 GetPointOnPath(float normalizedValue)
        {
            ThrowIfStale();

            if (!IsFinite(normalizedValue) || normalizedValue < 0f || normalizedValue > 1f)
                throw new ArgumentOutOfRangeException(nameof(normalizedValue));

            if (normalizedValue <= 0f)
                return _pathDataConfigs[0].PathData.GetPointOnPath(0f);

            if (normalizedValue >= 1f)
                return _pathDataConfigs[_pathDataConfigs.Count - 1].PathData.GetPointOnPath(1f);

            float targetDistance = normalizedValue * _totalPathLength;
            int pathIndex = FindPathIndex(targetDistance);

            if (pathIndex < 0 || pathIndex >= _pathDataConfigs.Count)
                throw new InvalidOperationException("MultiPathData distance cache is inconsistent.");

            float localDistance = targetDistance - _cumulativePathLengths[pathIndex];
            float localNormalizedValue = _pathLengths[pathIndex] > 0f 
                ? localDistance / _pathLengths[pathIndex] 
                : 0f;

            return _pathDataConfigs[pathIndex].PathData.GetPointOnPath(localNormalizedValue);
        }

        /// <summary>
        /// 거리 값을 전체 경로 길이에 비례해 0~1로 정규화하여 경로 상의 위치를 가져옵니다
        /// </summary>
        /// <param name="distance">거리 값 (전체 PathLength 기준)</param>
        /// <returns>경로 상의 위치</returns>
        public Vector3 GetPointAtDistance(float distance)
        {
            ThrowIfStale();
            if (!IsFinite(distance) || distance < 0f || distance > _totalPathLength)
                throw new ArgumentOutOfRangeException(nameof(distance));
            if (_totalPathLength <= 0f)
                throw new InvalidOperationException("MultiPathData has no measurable path length.");

            float normalizedValue = distance / _totalPathLength;
            return GetPointOnPath(normalizedValue);
        }

        /// <summary>
        /// 특정 PathData의 시작 지점을 전체 경로 기준 정규화된 값으로 가져옵니다
        /// </summary>
        /// <param name="pathIndex">PathData 인덱스</param>
        /// <returns>정규화된 시작 위치 (0~1)</returns>
        public float GetPathStartNormalizedValue(int pathIndex)
        {
            ThrowIfStale();

            if (pathIndex < 0 || pathIndex >= _cumulativePathLengths.Length)
                throw new ArgumentOutOfRangeException(nameof(pathIndex));

            return _totalPathLength > 0f 
                ? _cumulativePathLengths[pathIndex] / _totalPathLength 
                : 0f;
        }

        /// <summary>
        /// 특정 PathData의 끝 지점을 전체 경로 기준 정규화된 값으로 가져옵니다
        /// </summary>
        /// <param name="pathIndex">PathData 인덱스</param>
        /// <returns>정규화된 끝 위치 (0~1)</returns>
        public float GetPathEndNormalizedValue(int pathIndex)
        {
            ThrowIfStale();

            if (pathIndex < 0 || pathIndex >= _pathLengths.Length)
                throw new ArgumentOutOfRangeException(nameof(pathIndex));

            float endDistance = _cumulativePathLengths[pathIndex] + _pathLengths[pathIndex];
            return _totalPathLength > 0f 
                ? endDistance / _totalPathLength 
                : 0f;
        }

        /// <summary>
        /// 할당된 모든 <see cref="PathData"/>의 경로 이벤트를 <see cref="PathEventEntry.NormalizedTime"/> 오름차순으로 정렬합니다.
        /// 동일한 PathData가 여러 슬롯에 있으면 한 번만 정렬합니다.
        /// </summary>
        /// <returns>한 개 이상의 PathData에서 순서가 바뀌었으면 true</returns>
        public bool SortAllPathEventsByNormalizedTime()
        {
            ThrowIfStale();
            if (_pathDataConfigs.Count == 0)
                throw new InvalidOperationException("MultiPathData contains no path segments.");

            bool anyOrderChanged = false;
            HashSet<PathData> seen = new HashSet<PathData>();

            foreach (PathDataConfig config in _pathDataConfigs)
            {
                if (config == null || config.PathData == null)
                    throw new ArgumentException("MultiPathData contains an invalid PathDataConfig.", nameof(_pathDataConfigs));
                PathData pathData = config.PathData;
                if (!seen.Add(pathData))
                    continue;

                if (pathData.SortPathEventsByNormalizedTime())
                    anyOrderChanged = true;
            }

            return anyOrderChanged;
        }

        #endregion


        #region Path Sequence Helpers

        /// <summary>
        /// PathDataConfig 리스트로 초기화하는 공통 로직
        /// </summary>
        /// <param name="configs">PathDataConfig 리스트</param>
        private void BuildFromConfigs(List<PathDataConfig> configs)
        {
            if (!CollectValidConfigs(configs, out int validPathCount))
                throw new ArgumentException("MultiPathData contains no valid PathData entries.", nameof(configs));

            float nextTotalPathLength = 0f;
            for (int i = 0; i < validPathCount; i++)
            {
                ValidateConfig(_validConfigsScratch[i]);
                nextTotalPathLength += _validPathsScratch[i].PathLength;
            }

            if (!IsFinite(nextTotalPathLength) || nextTotalPathLength < 0f)
                throw new ArgumentOutOfRangeException(nameof(nextTotalPathLength));

            bool resultChanged = !IsSameSequenceResult(
                _validConfigsScratch,
                _validPathsScratch,
                nextTotalPathLength);

            _pathDataConfigs = new List<PathDataConfig>(_validConfigsScratch);
            _pathLengths = new float[validPathCount];
            _cumulativePathLengths = new float[validPathCount];
            _totalPathLength = 0f;

            for (int i = 0; i < validPathCount; i++)
            {
                _pathLengths[i] = _validPathsScratch[i].PathLength;
                _cumulativePathLengths[i] = _totalPathLength;
                _totalPathLength += _pathLengths[i];
            }

            _isInitialized = true;
            _fault = null;
            CaptureChildRevisions();
            NotifySequenceBuild(resultChanged);

#if UNITY_EDITOR
            Debug.Log($"MultiPathData 초기화 완료: PathData 개수={validPathCount}, 총 경로 길이={_totalPathLength:F2}m");
#endif
        }

        private bool IsSameSequenceResult(
            List<PathDataConfig> nextConfigs,
            List<PathData> nextPaths,
            float nextTotalPathLength)
        {
            if (!_isInitialized || _pathDataConfigs == null || _pathLengths == null)
                return false;

            if (_pathDataConfigs.Count != nextConfigs.Count || _pathLengths.Length != nextPaths.Count)
                return false;

            if (!Mathf.Approximately(_totalPathLength, nextTotalPathLength))
                return false;

            for (int i = 0; i < nextPaths.Count; i++)
            {
                PathDataConfig previousConfig = _pathDataConfigs[i];
                PathDataConfig nextConfig = nextConfigs[i];

                if (previousConfig == null || nextConfig == null)
                    return previousConfig == nextConfig;

                if (previousConfig.PathData != nextConfig.PathData
                    || previousConfig.MoveType != nextConfig.MoveType
                    || !Mathf.Approximately(previousConfig.Value, nextConfig.Value)
                    || previousConfig.TimeCurve != nextConfig.TimeCurve)
                    return false;

                if (!Mathf.Approximately(_pathLengths[i], nextPaths[i].PathLength))
                    return false;
            }

            return !_sequenceDirty;
        }

        private bool CollectValidConfigs(List<PathDataConfig> configs, out int validPathCount)
        {
            _validConfigsScratch.Clear();
            _validPathsScratch.Clear();
            validPathCount = 0;

            if (configs == null)
                return false;

            foreach (PathDataConfig config in configs)
            {
                if (config == null)
                    throw new ArgumentException("PathDataConfig cannot be null.", nameof(configs));
                if (config.PathData == null)
                    throw new ArgumentException("PathDataConfig requires a PathData reference.", nameof(configs));
                if (!config.PathData.IsInitialized || !config.PathData.IsReady)
                    throw new InvalidOperationException("Every child PathData must be initialized and ready before MultiPathData.");
                _validConfigsScratch.Add(config);
                _validPathsScratch.Add(config.PathData);
            }

            validPathCount = _validPathsScratch.Count;
            return validPathCount > 0;
        }

        /// <summary>
        /// 목표 거리에 해당하는 PathData 인덱스를 찾습니다
        /// </summary>
        /// <param name="targetDistance">목표 거리</param>
        /// <returns>PathData 인덱스</returns>
        private int FindPathIndex(float targetDistance)
        {
            if (_cumulativePathLengths == null || _cumulativePathLengths.Length == 0)
                return -1;

            int left = 0;
            int right = _cumulativePathLengths.Length - 1;

            while (left < right)
            {
                int mid = (left + right + 1) / 2;
                if (_cumulativePathLengths[mid] <= targetDistance)
                    left = mid;
                else
                    right = mid - 1;
            }

            return left;
        }

        #endregion

        #region Provider Contract State

        private int _revision = 0;
        private bool _sequenceDirty = false;
        private int[] _pathRevisionSnapshot = Array.Empty<int>();
        private readonly List<PathData> _subscribedPathData = new List<PathData>();

        #endregion


        #region Provider Contract

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;
        public bool IsReady => _isInitialized
            && !_isFaulted
            && !_sequenceDirty
            && _pathLengths != null
            && _pathLengths.Length > 0
            && !HasChildRevisionChanged();
        public int Revision => _revision;
        public int SegmentCount
        {
            get
            {
                ThrowIfStale();
                return _pathLengths.Length;
            }
        }

        public event Action PathChanged;

        #endregion


        #region Provider Lifecycle and API

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
        }

        private void OnEnable()
        {
            SubscribeToChildren();
        }

        private void OnDisable()
        {
            UnsubscribeFromChildren();
        }

        public Vector3 Sample(float normalizedTime)
        {
            ThrowIfStale();
            if (!IsFinite(normalizedTime) || normalizedTime < 0f || normalizedTime > 1f)
                throw new ArgumentOutOfRangeException(nameof(normalizedTime));
            return GetPointOnPath(normalizedTime);
        }

        public Vector3 SampleDistance(float distance)
        {
            ThrowIfStale();
            if (!IsFinite(distance) || distance < 0f || distance > _totalPathLength)
                throw new ArgumentOutOfRangeException(nameof(distance));
            return GetPointAtDistance(distance);
        }

        public PathSegmentDescriptor GetSegment(int index)
        {
            ThrowIfStale();
            if (index < 0 || index >= _pathDataConfigs.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            PathDataConfig config = _pathDataConfigs[index];
            if (config == null || config.PathData == null || !config.PathData.IsReady)
                throw new InvalidOperationException("MultiPathData contains an unready segment.");

            return new PathSegmentDescriptor(
                config.PathData,
                PathTypeConversion.ToPublic(config.MoveType),
                config.Value,
                config.TimeCurve);
        }

        public void Rebuild()
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("MultiPathData must be initialized before Rebuild.");
            try
            {
                BuildFromConfigs(_pathDataConfigs);
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

        internal void MarkSequenceDirty()
        {
            if (_isFaulted)
                throw new InvalidOperationException("MultiPathData is faulted. Call Release before changing its configuration.", _fault);
            _sequenceDirty = true;
        }

        internal void NotifySequenceBuild(bool resultChanged)
        {
            if (!resultChanged)
                return;

            _revision++;
            PathChanged?.Invoke();
        }

        #endregion


        #region Provider Helpers

        private void SubscribeToChildren()
        {
            UnsubscribeFromChildren();

            if (_pathDataConfigs == null)
                return;

            for (int i = 0; i < _pathDataConfigs.Count; i++)
            {
                PathDataConfig config = _pathDataConfigs[i];
                if (config == null || config.PathData == null)
                {
                    if (_isInitialized)
                        throw new InvalidOperationException($"MultiPathData segment {i} is missing its PathData reference.");
                    continue;
                }

                PathData pathData = config.PathData;
                if (_subscribedPathData.Contains(pathData))
                    continue;

                pathData.PathChanged += MarkSequenceDirty;
                _subscribedPathData.Add(pathData);
            }
        }

        private void UnsubscribeFromChildren()
        {
            for (int i = 0; i < _subscribedPathData.Count; i++)
            {
                PathData pathData = _subscribedPathData[i];
                if (pathData != null)
                    pathData.PathChanged -= MarkSequenceDirty;
            }

            _subscribedPathData.Clear();
        }

        private void CaptureChildRevisions()
        {
            if (_pathDataConfigs == null)
                throw new InvalidOperationException("MultiPathData segment configuration is missing.");

            int count = _pathDataConfigs.Count;
            if (_pathRevisionSnapshot.Length != count)
                _pathRevisionSnapshot = new int[count];

            for (int i = 0; i < count; i++)
            {
                PathDataConfig config = _pathDataConfigs[i];
                if (config == null)
                    throw new ArgumentException($"MultiPathData segment {i} is null.", nameof(_pathDataConfigs));
                if (config.PathData == null)
                    throw new ArgumentException($"MultiPathData segment {i} has no PathData reference.", nameof(_pathDataConfigs));
                _pathRevisionSnapshot[i] = config.PathData.Revision;
            }

            _sequenceDirty = false;
            SubscribeToChildren();
        }

        private void ThrowIfStale()
        {
            ThrowIfFaulted();
            if (!_isInitialized)
                throw new InvalidOperationException("MultiPathData is not initialized.");
            if (_sequenceDirty || HasChildRevisionChanged())
                throw new InvalidOperationException("MultiPathData is stale. Call Rebuild before querying it.");
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("MultiPathData has not been initialized.");
            UnsubscribeFromChildren();
            _isInitialized = false;
            _isFaulted = false;
            _sequenceDirty = false;
            _fault = null;
            _pathLengths = null;
            _cumulativePathLengths = null;
            _pathRevisionSnapshot = Array.Empty<int>();
            _totalPathLength = -1f;
        }

        private void ThrowIfFaulted()
        {
            if (_isFaulted)
                throw new InvalidOperationException("MultiPathData is faulted. Call Release before using it.", _fault);
        }

        private static void ValidateConfig(PathDataConfig config)
        {
            if (config == null)
                throw new ArgumentException("PathDataConfig cannot be null.");
            if (!IsFinite(config.Value) || config.Value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(config.Value));
            if (!Enum.IsDefined(typeof(PathFollower.EMoveType), config.MoveType))
                throw new ArgumentOutOfRangeException(nameof(config.MoveType));
            if (config.MoveType == PathFollower.EMoveType.TimeBased && config.TimeCurve == null)
                throw new ArgumentNullException(nameof(config.TimeCurve));
            if (config.MoveType == PathFollower.EMoveType.TimeBased && config.TimeCurve.length == 0)
                throw new ArgumentException("Time-based segments require a non-empty TimeCurve.", nameof(config.TimeCurve));
        }

        private bool HasChildRevisionChanged()
        {
            if (_pathDataConfigs == null || _pathRevisionSnapshot.Length != _pathDataConfigs.Count)
                return true;

            for (int i = 0; i < _pathDataConfigs.Count; i++)
            {
                PathDataConfig config = _pathDataConfigs[i];
                if (config == null)
                    throw new InvalidOperationException($"MultiPathData segment {i} is null.");
                if (config.PathData == null)
                    throw new InvalidOperationException($"MultiPathData segment {i} has no PathData reference.");
                int revision = config.PathData.Revision;
                if (_pathRevisionSnapshot[i] != revision)
                    return true;
            }

            return false;
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        #endregion
}
}
