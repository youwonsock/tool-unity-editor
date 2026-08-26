using System;
using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    /// <summary>
    /// 여러 PathData를 하나의 경로처럼 관리하고 사용할 수 있게 해주는 클래스
    /// </summary>
    public partial class MultiPathData : MonoBehaviour
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


        #region Member Variables

        [SerializeField] private List<PathDataConfig> _pathDataConfigs = new List<PathDataConfig>();
        
        private float[] _pathLengths;
        private float[] _cumulativePathLengths;
        private float _totalPathLength = -1f;
        private bool _isInitialized = false;

        private readonly List<PathDataConfig> _validConfigsScratch = new List<PathDataConfig>();
        private readonly List<PathData> _validPathsScratch = new List<PathData>();

        #endregion


        #region Properties

        /// <summary>
        /// 관리 중인 PathDataConfig 리스트
        /// </summary>
        public List<PathDataConfig> PathDataConfigs => _pathDataConfigs;

        /// <summary>
        /// 전체 경로의 총 길이
        /// </summary>
        public float PathLength
        {
            get
            {
                EnsureCurrent();
                return _totalPathLength;
            }
        }

        /// <summary>
        /// 관리 중인 PathData 개수
        /// </summary>
        public int PathCount => _pathDataConfigs?.Count ?? 0;

        #endregion


        #region Unity Events

        private void OnValidate()
        {
            if (_pathDataConfigs == null)
                return;

            MarkSequenceDirty();

            for (int i = 0; i < _pathDataConfigs.Count; i++)
            {
                PathDataConfig config = _pathDataConfigs[i];
                if (config == null)
                    continue;

                config.Value = Mathf.Max(0f, config.Value);
                if (config.TimeCurve == null)
                    config.TimeCurve = AnimationCurve.Linear(0, 0, 1, 1);
            }
        }

        #endregion


        #region Public Methods

        /// <summary>
        /// MultiPathData를 초기화합니다
        /// </summary>
        /// <param name="forceReinit">강제 재초기화 여부</param>
        public void Init(bool forceReinit = false)
        {
            if (_isInitialized && !forceReinit)
                return;

            if (_pathDataConfigs == null || _pathDataConfigs.Count == 0)
            {
                SetInvalidState("MultiPathData: PathDataConfig 리스트가 비어있습니다!");
                return;
            }

            InitializeWithConfigs(_pathDataConfigs, forceReinit);
        }

        /// <summary>
        /// 외부에서 제공된 PathDataConfig 리스트로 초기화합니다
        /// </summary>
        /// <param name="pathDataConfigs">PathDataConfig 리스트</param>
        /// <param name="forceReinit">강제 재초기화 여부</param>
        public void Init(List<PathDataConfig> pathDataConfigs, bool forceReinit = false)
        {
            if (_isInitialized && !forceReinit)
                return;

            if (pathDataConfigs == null || pathDataConfigs.Count == 0)
            {
                SetInvalidState("MultiPathData: 제공된 PathDataConfig 리스트가 비어있습니다!");
                return;
            }

            _pathDataConfigs = new List<PathDataConfig>(pathDataConfigs);
            InitializeWithConfigs(_pathDataConfigs, forceReinit);
        }

        /// <summary>
        /// 외부에서 제공된 PathData 리스트로 초기화합니다 (기본 설정 사용)
        /// </summary>
        /// <param name="pathDataList">PathData 리스트</param>
        /// <param name="forceReinit">강제 재초기화 여부</param>
        public void Init(List<PathData> pathDataList, bool forceReinit = false)
        {
            if (_isInitialized && !forceReinit)
                return;

            if (pathDataList == null || pathDataList.Count == 0)
            {
                SetInvalidState("MultiPathData: 제공된 PathData 리스트가 비어있습니다!");
                return;
            }

            List<PathDataConfig> configs = new List<PathDataConfig>();
            foreach (PathData pathData in pathDataList)
            {
                if (pathData != null)
                {
                    PathDataConfig config = new PathDataConfig
                    {
                        PathData = pathData,
                        MoveType = PathFollower.EMoveType.TimeBased,
                        Value = 1f,
                        TimeCurve = AnimationCurve.Linear(0, 0, 1, 1)
                    };
                    configs.Add(config);
                }
            }

            _pathDataConfigs = configs;
            InitializeWithConfigs(_pathDataConfigs, forceReinit);
        }

        /// <summary>
        /// 외부에서 제공된 PathData 배열로 초기화합니다 (기본 설정 사용)
        /// </summary>
        /// <param name="pathDataArray">PathData 배열</param>
        /// <param name="forceReinit">강제 재초기화 여부</param>
        public void Init(PathData[] pathDataArray, bool forceReinit = false)
        {
            if (_isInitialized && !forceReinit)
                return;

            if (pathDataArray == null || pathDataArray.Length == 0)
            {
                SetInvalidState("MultiPathData: 제공된 PathData 배열이 비어있습니다!");
                return;
            }

            List<PathData> pathDataList = new List<PathData>(pathDataArray);
            Init(pathDataList, forceReinit);
        }

        /// <summary>
        /// 0~1의 정규화된 값으로 전체 경로 상의 위치를 가져옵니다
        /// </summary>
        /// <param name="normalizedValue">정규화된 진행도 (0~1)</param>
        /// <returns>경로 상의 위치</returns>
        public Vector3 GetPointOnPath(float normalizedValue)
        {
            EnsureCurrent();

            if (_pathDataConfigs == null || _pathDataConfigs.Count == 0)
                return Vector3.zero;

            normalizedValue = Mathf.Clamp01(normalizedValue);

            if (normalizedValue <= 0f)
                return _pathDataConfigs[0].PathData.GetPointOnPath(0f);

            if (normalizedValue >= 1f)
                return _pathDataConfigs[_pathDataConfigs.Count - 1].PathData.GetPointOnPath(1f);

            float targetDistance = normalizedValue * _totalPathLength;
            int pathIndex = FindPathIndex(targetDistance);

            if (pathIndex < 0 || pathIndex >= _pathDataConfigs.Count)
                return _pathDataConfigs[_pathDataConfigs.Count - 1].PathData.GetPointOnPath(1f);

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
            if (PathLength <= 0f)
            {
                Debug.LogWarning("MultiPathData: PathLength가 0 이하입니다.");
                return Vector3.zero;
            }

            float normalizedValue = distance / PathLength;
            return GetPointOnPath(normalizedValue);
        }

        /// <summary>
        /// 특정 PathData의 시작 지점을 전체 경로 기준 정규화된 값으로 가져옵니다
        /// </summary>
        /// <param name="pathIndex">PathData 인덱스</param>
        /// <returns>정규화된 시작 위치 (0~1)</returns>
        public float GetPathStartNormalizedValue(int pathIndex)
        {
            EnsureCurrent();

            if (pathIndex < 0 || pathIndex >= _cumulativePathLengths.Length)
                return 0f;

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
            EnsureCurrent();

            if (pathIndex < 0 || pathIndex >= _pathLengths.Length)
                return 0f;

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
            if (_pathDataConfigs == null || _pathDataConfigs.Count == 0)
                return false;

            bool anyOrderChanged = false;
            HashSet<PathData> seen = new HashSet<PathData>();

            foreach (PathDataConfig config in _pathDataConfigs)
            {
                PathData pathData = config?.PathData;
                if (pathData == null || !seen.Add(pathData))
                    continue;

                if (pathData.SortPathEventsByNormalizedTime())
                    anyOrderChanged = true;
            }

            return anyOrderChanged;
        }

        #endregion


        #region Private Methods

        /// <summary>
        /// PathDataConfig 리스트로 초기화하는 공통 로직
        /// </summary>
        /// <param name="configs">PathDataConfig 리스트</param>
        /// <param name="forceReinit">강제 재초기화 여부</param>
        private void InitializeWithConfigs(List<PathDataConfig> configs, bool forceReinit)
        {
            if (!CollectValidConfigs(configs, forceReinit, out int validPathCount))
            {
                SetInvalidState("MultiPathData: 유효한 PathData가 없습니다!", _validConfigsScratch);
                return;
            }

            float nextTotalPathLength = 0f;
            for (int i = 0; i < validPathCount; i++)
                nextTotalPathLength += _validPathsScratch[i].PathLength;

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

        private bool CollectValidConfigs(List<PathDataConfig> configs, bool forceReinit, out int validPathCount)
        {
            _validConfigsScratch.Clear();
            _validPathsScratch.Clear();
            validPathCount = 0;

            if (configs == null)
                return false;

            foreach (PathDataConfig config in configs)
            {
                if (config == null || config.PathData == null)
                    continue;

                PathDataInitialization.Initialize(config.PathData, forceReinit);
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

        private void SetInvalidState(string warningMessage, List<PathDataConfig> fallbackConfigs = null)
        {
            if (!string.IsNullOrEmpty(warningMessage))
                Debug.LogWarning(warningMessage);

            bool wasReady = IsReady;
            _isInitialized = false;
            _pathDataConfigs = fallbackConfigs ?? _pathDataConfigs ?? new List<PathDataConfig>();
            _pathLengths = Array.Empty<float>();
            _cumulativePathLengths = Array.Empty<float>();
            _totalPathLength = 0f;
            _sequenceDirty = false;
            _pathRevisionSnapshot = Array.Empty<int>();
            NotifySequenceBuild(wasReady);
        }

        #endregion


    }
}
