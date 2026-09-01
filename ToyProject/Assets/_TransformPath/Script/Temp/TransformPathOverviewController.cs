using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath.Samples
{
    /// <summary>
    /// TransformPath 공개 기능을 하나의 통합 쇼케이스에서 조작합니다.
    /// </summary>
    [DefaultExecutionOrder(10)]
    public sealed class TransformPathOverviewController : MonoBehaviour
    {
        [Header("Serialized References")]
        [SerializeField] private PathData _pathData;
        [SerializeField] private PathFollower _pathFollower;
        [SerializeField] private MultiPathData _multiPathData;
        [SerializeField] private PathFollower _multiPathFollower;
        [SerializeField] private QueuedPathManager _queueManager;
        [SerializeField] private QueuedPathFollower[] _queueFollowers;
        [SerializeField] private TransformPathSampleMessageReceiver _messageReceiver;
        [SerializeField] private TransformPathOverviewBoard _board;

        private bool _isInitialized;
        private bool _isFaulted;
        private Exception _fault;
        private bool _queueVisible = true;
        private int _presentationMode;
        private readonly List<Vector3> _controlPointBuffer = new List<Vector3>();
        private Vector3 _normalizedSample;
        private Vector3 _distanceSample;
        private bool _hasApiDiagnostics;
        private bool _hasFirstSegment;

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;
        public int PresentationMode => _presentationMode;
        public bool QueueVisible => _queueVisible;
        public PathData.ECurveType CurrentCurveType => _pathData != null ? _pathData.CurveType : throw new InvalidOperationException("TransformPath overview has no PathData.");
        public PathData.ESamplingType CurrentSamplingType => _pathData != null ? _pathData.SamplingType : throw new InvalidOperationException("TransformPath overview has no PathData.");
        public int ControlPointCount => _hasApiDiagnostics ? _controlPointBuffer.Count : throw new InvalidOperationException("TransformPath overview diagnostics are not ready.");
        public Vector3 LastNormalizedSample => _hasApiDiagnostics ? _normalizedSample : throw new InvalidOperationException("TransformPath overview diagnostics are not ready.");
        public Vector3 LastDistanceSample => _hasApiDiagnostics ? _distanceSample : throw new InvalidOperationException("TransformPath overview diagnostics are not ready.");
        public bool HasFirstSegment => _hasFirstSegment;

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("TransformPathOverviewController is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("TransformPathOverviewController is faulted; call Release before Init.", _fault);
            try
            {
                if (_pathData == null || _pathFollower == null || _multiPathData == null
                    || _multiPathFollower == null || _queueManager == null || _messageReceiver == null || _board == null)
                    throw new InvalidOperationException("TransformPath overview requires all serialized path, follower, queue, receiver, and board references.");
                if (!_pathData.IsInitialized || !_pathData.IsReady)
                    throw new InvalidOperationException("TransformPath overview PathData must be initialized and ready.");
                if (!_pathFollower.IsInitialized || !_multiPathData.IsInitialized || !_multiPathData.IsReady)
                    throw new InvalidOperationException("TransformPath overview followers and MultiPathData must be initialized and ready.");
                if (!_multiPathFollower.IsInitialized || !_queueManager.IsInitialized)
                    throw new InvalidOperationException("TransformPath overview MultiPath follower and Queue Manager must be initialized.");
                if (_queueFollowers == null || _queueFollowers.Length != 3)
                    throw new ArgumentException("TransformPath overview requires exactly three queue followers.", nameof(_queueFollowers));
                for (int i = 0; i < _queueFollowers.Length; i++)
                {
                    if (_queueFollowers[i] == null || !_queueFollowers[i].IsInitialized)
                        throw new InvalidOperationException($"Queue follower {i} is not initialized.");
                }
                RefreshApiDiagnostics();
                _isInitialized = true;
            }
            catch (Exception exception)
            {
                _fault = exception;
                _isFaulted = true;
                throw;
            }
        }

        private void Start()
        {
            ThrowIfUnavailable();
            if (!_pathFollower.IsMoving)
                _pathFollower.StartMove(_pathData);
            if (!_multiPathFollower.IsMoving)
                _multiPathFollower.StartMove(_multiPathData, RestartMultiPath, null);
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                if (!_queueFollowers[i].IsMoving)
                    _queueFollowers[i].StartMove(_multiPathData, RestartQueueFollower);
            }
            RenderBoard();
        }

        private void Update()
        {
            ThrowIfUnavailable();

            if (Input.GetKeyDown(KeyCode.Alpha1))
                SelectPresentationMode(0);
            if (Input.GetKeyDown(KeyCode.Alpha2))
                SelectPresentationMode(1);
            if (Input.GetKeyDown(KeyCode.Alpha3))
                SelectPresentationMode(2);
            if (Input.GetKeyDown(KeyCode.Q))
                _queueVisible = !_queueVisible;
            if (Input.GetKeyDown(KeyCode.Space))
                TogglePause();
            if (Input.GetKeyDown(KeyCode.R))
                ResetFollowers();
            if (Input.GetKeyDown(KeyCode.LeftArrow))
                _pathFollower.Seek(Mathf.Clamp01(_pathFollower.NormalizedTime - 0.1f));
            if (Input.GetKeyDown(KeyCode.RightArrow))
                _pathFollower.Seek(Mathf.Clamp01(_pathFollower.NormalizedTime + 0.1f));
            if (Input.GetKeyDown(KeyCode.E))
                _messageReceiver.ReceivePathEvent("TransformPath.Sample.Manual", _pathFollower);

            RenderBoard();
        }

        /// <summary>
        /// 쇼케이스에서 실제 곡선·샘플링 조합을 선택합니다.
        /// </summary>
        public void SelectPresentationMode(int mode)
        {
            if (mode < 0 || mode > 2)
                throw new ArgumentOutOfRangeException(nameof(mode));

            bool wasMoving = _pathFollower.IsMoving;
            if (wasMoving)
                _pathFollower.PauseMove();

            PathData.ECurveType curveType;
            PathData.ESamplingType samplingType;
            switch (mode)
            {
                case 0:
                    curveType = PathData.ECurveType.Linear;
                    samplingType = PathData.ESamplingType.Uniform;
                    break;
                case 1:
                    curveType = PathData.ECurveType.SplineInterpolating;
                    samplingType = PathData.ESamplingType.DistanceBased;
                    break;
                case 2:
                    curveType = PathData.ECurveType.SplineApproximating;
                    samplingType = PathData.ESamplingType.Random;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }

            _pathData.SetCurveType(curveType);
            _pathData.SetSamplingType(samplingType);
            _pathData.Rebuild();
            RefreshApiDiagnostics();
            _pathFollower.ResetToStart();
            if (wasMoving)
                _pathFollower.ResumeMove();
            _presentationMode = mode;
        }

        private void TogglePause()
        {
            if (_pathFollower.IsMoving)
                _pathFollower.PauseMove();
            else
                _pathFollower.ResumeMove();

            if (_multiPathFollower.IsMoving)
                _multiPathFollower.PauseMove();
            else
                _multiPathFollower.ResumeMove();

            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                if (_queueFollowers[i].IsMoving)
                    _queueFollowers[i].PauseMove();
                else
                    _queueFollowers[i].ResumeMove();
            }
        }

        private void ResetFollowers()
        {
            _pathFollower.ResetToStart();
            _multiPathFollower.ResetToStart();
            for (int i = 0; i < _queueFollowers.Length; i++)
                _queueFollowers[i].PathFollower.ResetToStart();
        }

        private void RestartMultiPath()
        {
            ThrowIfUnavailable();
            if (!_multiPathFollower.IsMoving)
                _multiPathFollower.StartMove(_multiPathData, RestartMultiPath, null);
        }

        private void RestartQueueFollower()
        {
            ThrowIfUnavailable();
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                if (!_queueFollowers[i].IsMoving)
                {
                    _queueFollowers[i].StartMove(_multiPathData, RestartQueueFollower);
                    break;
                }
            }
        }

        private void RenderBoard()
        {
            if (!_board.IsInitialized)
                throw new InvalidOperationException("TransformPathOverviewBoard must be initialized before rendering.");
            bool queueRegistered = IsQueueFollowerRegistered(_queueFollowers[0]);
            float? queueDistance = queueRegistered
                ? _queueManager.GetDistanceToAhead(_queueFollowers[0])
                : null;
            _board.Render(
                "TRANSFORM PATH SHOWCASE\n"
                + $"Path ready={_pathData.IsReady}  length={_pathData.PathLength:F2}m  revision={_pathData.Revision}\n"
                + $"Follower progress={_pathFollower.NormalizedTime:F2}  moving={_pathFollower.IsMoving}\n"
                + $"MultiPath ready={_multiPathData.IsReady}  segments={_multiPathData.PathCount}  progress={_multiPathFollower.GlobalNormalizedTime:F2}\n"
                + $"API Sample={(_hasApiDiagnostics ? _normalizedSample.ToString("F1") : "pending")}  Distance={(_hasApiDiagnostics ? _distanceSample.ToString("F1") : "pending")}  Points={(_hasApiDiagnostics ? _controlPointBuffer.Count.ToString() : "pending")}  Seg0={_hasFirstSegment}\n"
                + $"Queue visible={_queueVisible}  registered={_queueManager.FollowerCount}  ahead distance={(queueDistance.HasValue ? queueDistance.Value.ToString("F2") : "none")}\n"
                + $"Message={_messageReceiver.LastMessage}  received={_messageReceiver.ReceivedCount}\n"
                + $"Mode={_presentationMode} ({_pathData.CurveType}/{_pathData.SamplingType})  Curve/Sampling: 1/2/3 | Q Queue | Space Pause | R Reset | ←/→ Seek | E Event");
        }

        private bool IsQueueFollowerRegistered(QueuedPathFollower follower)
        {
            if (follower == null)
                throw new ArgumentNullException(nameof(follower));
            IReadOnlyList<QueuedPathFollower> registered = _queueManager.Followers;
            for (int i = 0; i < registered.Count; i++)
            {
                if (ReferenceEquals(registered[i], follower))
                    return true;
            }
            return false;
        }

        private void RefreshApiDiagnostics()
        {
            if (_pathData == null || !_pathData.IsReady || _multiPathData == null || !_multiPathData.IsReady)
                throw new InvalidOperationException("PathData and MultiPathData must be ready before API diagnostics.");

            _pathData.CopyWorldControlPoints(_controlPointBuffer);
            _normalizedSample = _pathData.Sample(0.5f);
            _distanceSample = _pathData.SampleDistance(_pathData.PathLength * 0.5f);
            PathSegmentDescriptor firstSegment = _multiPathData.GetSegment(0);
            _hasFirstSegment = firstSegment.Provider != null && firstSegment.Provider.IsReady;
            _hasApiDiagnostics = true;
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("TransformPathOverviewController has not been initialized.");
            if (_isInitialized)
            {
                _pathFollower.StopMove();
                _multiPathFollower.StopMove();
                for (int i = 0; i < _queueFollowers.Length; i++)
                    _queueFollowers[i].StopMove();
            }
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
        }

        private void ThrowIfUnavailable()
        {
            if (_isFaulted)
                throw new InvalidOperationException("TransformPathOverviewController is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("TransformPathOverviewController is not initialized.");
        }
    }
}
