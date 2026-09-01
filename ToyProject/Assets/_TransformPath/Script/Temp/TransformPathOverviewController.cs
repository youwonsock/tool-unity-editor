using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath.Samples
{
    public enum TransformPathShowcaseLane
    {
        Normal = 0,
        MultiPath = 1,
        Queue = 2,
    }

    /// <summary>
    /// TransformPath 샘플을 일반 경로, MultiPath, Queue lane으로 분리해 조작합니다.
    /// </summary>
    [DefaultExecutionOrder(10)]
    public sealed class TransformPathOverviewController : MonoBehaviour
    {
        [Header("Normal Path Lane")]
        [SerializeField] private PathData _pathData;
        [SerializeField] private PathFollower _pathFollower;

        [Header("Multi Path Lane")]
        [SerializeField] private MultiPathData _multiPathData;
        [SerializeField] private PathFollower _multiPathFollower;

        [Header("Queued Path Lane")]
        [SerializeField] private QueuedPathManager _queueManager;
        [SerializeField] private QueuedPathFollower[] _queueFollowers;

        [Header("Presentation")]
        [SerializeField] private TransformPathFreeCamera _freeCamera;

        [Header("Diagnostics")]
        [SerializeField] private TransformPathSampleMessageReceiver _messageReceiver;
        [SerializeField] private TransformPathOverviewBoard _board;

        private readonly List<Vector3> _controlPointBuffer = new List<Vector3>();
        private readonly bool[] _queueWasMovingWhenHidden = new bool[3];
        private readonly bool[] _queueWasMovingBeforeGlobalPause = new bool[3];
        private MultiPathData _queuePathData;
        private PathData _queuePath;
        private GameObject _queueRouteDataObject;
        private GameObject _queueRouteLineObject;
        private LineRenderer _queueRouteRenderer;
        private Material _queueRouteMaterial;
        private readonly List<LineRenderer> _multiRouteRenderers = new List<LineRenderer>();
        private Material _multiRouteMaterial;
        private Vector3 _normalizedSample;
        private Vector3 _distanceSample;
        private bool _hasApiDiagnostics;
        private bool _hasFirstSegment;
        private bool _isInitialized;
        private bool _isFaulted;
        private Exception _fault;
        private bool _queueVisible = true;
        private int _presentationMode;
        private TransformPathShowcaseLane _activeLane = TransformPathShowcaseLane.Normal;
        private bool _isResettingQueue;
        private bool _isPaused;
        private bool _normalWasMovingWhenHidden;
        private bool _multiWasMovingWhenHidden;
        private bool _normalWasMovingBeforeGlobalPause;
        private bool _multiWasMovingBeforeGlobalPause;

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;
        public bool IsPaused => _isPaused;
        public int PresentationMode => _presentationMode;
        public bool QueueVisible => _queueVisible;
        public TransformPathShowcaseLane ActiveLane => _activeLane;
        public MultiPathData QueuePathData => _queuePathData;
        public PathData.ECurveType CurrentCurveType => _pathData != null
            ? _pathData.CurveType
            : throw new InvalidOperationException("TransformPath overview has no PathData.");
        public PathData.ESamplingType CurrentSamplingType => _pathData != null
            ? _pathData.SamplingType
            : throw new InvalidOperationException("TransformPath overview has no PathData.");
        public int ControlPointCount => _hasApiDiagnostics
            ? _controlPointBuffer.Count
            : throw new InvalidOperationException("TransformPath overview diagnostics are not ready.");
        public Vector3 LastNormalizedSample => _hasApiDiagnostics
            ? _normalizedSample
            : throw new InvalidOperationException("TransformPath overview diagnostics are not ready.");
        public Vector3 LastDistanceSample => _hasApiDiagnostics
            ? _distanceSample
            : throw new InvalidOperationException("TransformPath overview diagnostics are not ready.");
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
                ValidateSerializedReferences();
                CreateQueueRoute();

                if (!_pathData.IsInitialized || !_pathData.IsReady)
                    throw new InvalidOperationException("Normal PathData must be initialized and ready.");
                if (!_pathFollower.IsInitialized)
                    throw new InvalidOperationException("Normal PathFollower must be initialized.");
                if (!_multiPathData.IsInitialized || !_multiPathData.IsReady || !_multiPathFollower.IsInitialized)
                    throw new InvalidOperationException("MultiPath lane must be initialized and ready.");
                if (!_queueManager.IsInitialized || !_queuePathData.IsInitialized || !_queuePathData.IsReady)
                    throw new InvalidOperationException("Queue lane must be initialized and ready.");
                if (_queueFollowers == null || _queueFollowers.Length != 3)
                    throw new ArgumentException("TransformPath overview requires exactly three queue followers.", nameof(_queueFollowers));

                _queueManager.MultiPathData = _queuePathData;
                CreateMultiPathRouteLines();
                for (int i = 0; i < _queueFollowers.Length; i++)
                {
                    if (_queueFollowers[i] == null || !_queueFollowers[i].IsInitialized)
                        throw new InvalidOperationException($"Queue follower {i} is not initialized.");
                }

                ResolveFreeCamera();
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
            StartNormalPath();
            StartMultiPath();
            StartQueueFollowers();

            // Initialize and start all providers once, then pause and hide the
            // lanes that are not selected for the current presentation.
            PauseLane(TransformPathShowcaseLane.MultiPath);
            PauseLane(TransformPathShowcaseLane.Queue);
            ApplyLanePresentation();
            FocusActiveLane();
        }

        private void Update()
        {
            ThrowIfUnavailable();

            bool cameraConsumesSharedKeys = _freeCamera != null && _freeCamera.IsLookMode;
            if (Input.GetKeyDown(KeyCode.Alpha1))
                SelectPresentationMode(0);
            if (Input.GetKeyDown(KeyCode.Alpha2))
                SelectPresentationMode(1);
            if (Input.GetKeyDown(KeyCode.Alpha3))
                SelectPresentationMode(2);
            if (!cameraConsumesSharedKeys && Input.GetKeyDown(KeyCode.Q))
                ToggleQueueVisibility();
            if (Input.GetKeyDown(KeyCode.Space))
                TogglePauseAll();
            if (Input.GetKeyDown(KeyCode.R))
                ResetAll();
            if (Input.GetKeyDown(KeyCode.LeftArrow))
                SeekBackward();
            if (Input.GetKeyDown(KeyCode.RightArrow))
                SeekForward();
            if (!cameraConsumesSharedKeys && Input.GetKeyDown(KeyCode.E))
                FireTestEvent();
        }

        public void ShowNormalLane()
        {
            ThrowIfUnavailable();
            SetActiveLane(TransformPathShowcaseLane.Normal);
        }

        public void ShowMultiPathLane()
        {
            ThrowIfUnavailable();
            SetActiveLane(TransformPathShowcaseLane.MultiPath);
        }

        public void ShowQueuedLane()
        {
            ThrowIfUnavailable();
            SetActiveLane(TransformPathShowcaseLane.Queue);
        }

        public void FocusActiveLane()
        {
            ThrowIfUnavailable();
            if (_freeCamera == null)
                return;

            _freeCamera.FocusOnBounds(GetActiveLaneBounds());
        }

        /// <summary>
        /// 일반 경로에서 curve와 sampling 조합을 선택합니다.
        /// </summary>
        public void SelectPresentationMode(int mode)
        {
            ThrowIfUnavailable();
            if (mode < 0 || mode > 2)
                throw new ArgumentOutOfRangeException(nameof(mode));

            bool wasMoving = _pathFollower.IsMoving;
            if (wasMoving)
                _pathFollower.PauseMove();

            switch (mode)
            {
                case 0:
                    _pathData.SetCurveType(PathData.ECurveType.Linear);
                    _pathData.SetSamplingType(PathData.ESamplingType.Uniform);
                    break;
                case 1:
                    _pathData.SetCurveType(PathData.ECurveType.SplineInterpolating);
                    _pathData.SetSamplingType(PathData.ESamplingType.DistanceBased);
                    break;
                case 2:
                    _pathData.SetCurveType(PathData.ECurveType.SplineApproximating);
                    _pathData.SetSamplingType(PathData.ESamplingType.Random);
                    break;
            }

            _pathData.Rebuild();
            RefreshApiDiagnostics();
            _pathFollower.ResetToStart();
            if (wasMoving)
                _pathFollower.ResumeMove();
            _presentationMode = mode;
        }

        public void TogglePauseAll()
        {
            ThrowIfUnavailable();
            if (_isPaused)
                ResumeAll();
            else
                PauseAll();
        }

        public void PauseAll()
        {
            ThrowIfUnavailable();
            if (_isPaused)
                return;

            _normalWasMovingBeforeGlobalPause = _activeLane == TransformPathShowcaseLane.Normal && _pathFollower.IsMoving;
            _multiWasMovingBeforeGlobalPause = _activeLane == TransformPathShowcaseLane.MultiPath && _multiPathFollower.IsMoving;
            for (int i = 0; i < _queueWasMovingBeforeGlobalPause.Length; i++)
            {
                _queueWasMovingBeforeGlobalPause[i] = _activeLane == TransformPathShowcaseLane.Queue
                    && _queueVisible
                    && _queueFollowers[i] != null
                    && _queueFollowers[i].IsMoving;
            }

            _pathFollower.PauseMove();
            _multiPathFollower.PauseMove();
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                if (_queueFollowers[i] != null)
                    _queueFollowers[i].PauseMove();
            }

            _isPaused = true;
        }

        public void ResumeAll()
        {
            ThrowIfUnavailable();
            if (!_isPaused)
                return;

            _isPaused = false;
            if (_activeLane == TransformPathShowcaseLane.Normal
                && (_normalWasMovingBeforeGlobalPause || _normalWasMovingWhenHidden))
            {
                _pathFollower.ResumeMove();
                _normalWasMovingWhenHidden = false;
            }
            else if (_activeLane == TransformPathShowcaseLane.MultiPath
                && (_multiWasMovingBeforeGlobalPause || _multiWasMovingWhenHidden))
            {
                _multiPathFollower.ResumeMove();
                _multiWasMovingWhenHidden = false;
            }
            else if (_activeLane == TransformPathShowcaseLane.Queue && _queueVisible)
            {
                for (int i = 0; i < _queueFollowers.Length; i++)
                {
                    if (_queueFollowers[i] != null
                        && (_queueWasMovingBeforeGlobalPause[i] || _queueWasMovingWhenHidden[i]))
                        _queueFollowers[i].ResumeMove();
                    _queueWasMovingWhenHidden[i] = false;
                }
            }

            _normalWasMovingBeforeGlobalPause = false;
            _multiWasMovingBeforeGlobalPause = false;
            Array.Clear(_queueWasMovingBeforeGlobalPause, 0, _queueWasMovingBeforeGlobalPause.Length);
        }

        public void ResetAll()
        {
            ThrowIfUnavailable();

            bool wasPaused = _isPaused;
            _isPaused = false;

            if (_pathFollower.IsMoving)
                _pathFollower.StopMove();
            if (_multiPathFollower.IsMoving)
                _multiPathFollower.StopMove();

            StartNormalPath();
            StartMultiPath();

            _isResettingQueue = true;
            try
            {
                ResetQueueFollowerRegistrations();
                StartQueueFollowers();
            }
            finally
            {
                _isResettingQueue = false;
            }

            _normalWasMovingWhenHidden = false;
            _multiWasMovingWhenHidden = false;
            for (int i = 0; i < _queueWasMovingWhenHidden.Length; i++)
                _queueWasMovingWhenHidden[i] = true;

            PauseUnselectedLanes();
            ApplyLanePresentation();

            if (wasPaused)
                PauseAll();
        }

        public void SeekBackward() => SeekSelected(-0.1f);

        public void SeekForward() => SeekSelected(0.1f);

        public void SeekSelected(float delta)
        {
            ThrowIfUnavailable();
            if (!IsFinite(delta))
                throw new ArgumentOutOfRangeException(nameof(delta));

            if (_activeLane == TransformPathShowcaseLane.MultiPath)
            {
                _multiPathFollower.SetGlobalNormalizedTime(
                    Mathf.Clamp01(_multiPathFollower.GlobalNormalizedTime + delta));
                return;
            }
            if (_activeLane != TransformPathShowcaseLane.Normal)
                return;

            _pathFollower.Seek(Mathf.Clamp01(_pathFollower.NormalizedTime + delta));
        }

        public void BlockQueueLeader()
        {
            ThrowIfUnavailable();
            QueuedPathFollower leader = GetQueueLeader();
            if (leader != null)
                leader.ForceBlock();
        }

        public void UnblockQueueLeader()
        {
            ThrowIfUnavailable();
            QueuedPathFollower leader = GetQueueLeader();
            if (leader != null)
                leader.ForceUnblock();
        }

        public void FireTestEvent()
        {
            ThrowIfUnavailable();
            _messageReceiver.ReceivePathEvent("TransformPath.Sample.Manual", _pathFollower);
        }

        public void ToggleQueueVisibility()
        {
            ThrowIfUnavailable();
            SetQueueVisible(!_queueVisible);
        }

        /// <summary>
        /// 현재 선택 lane에 맞춘 짧은 상태 문자열을 UI가 표시할 수 있도록 제공합니다.
        /// </summary>
        public string GetActiveStatusText()
        {
            ThrowIfUnavailable();
            switch (_activeLane)
            {
                case TransformPathShowcaseLane.MultiPath:
                    return GetMultiPathStatusText();
                case TransformPathShowcaseLane.Queue:
                    return GetQueueStatusText();
                default:
                    return GetNormalStatusText();
            }
        }

        private string GetNormalStatusText()
        {
            return $"ready={_pathData.IsReady}  length={_pathData.PathLength:F2}m  progress={_pathFollower.GlobalNormalizedTime:F2}\n"
                + $"curve={_pathData.CurveType}\n"
                + $"sampling={_pathData.SamplingType}  points={_controlPointBuffer.Count}\n"
                + $"event={_messageReceiver.LastMessage}  received={_messageReceiver.ReceivedCount}";
        }

        private string GetMultiPathStatusText()
        {
            int segmentCount = _multiPathData.PathCount;
            int segmentIndex = segmentCount > 0
                ? Mathf.Clamp(_multiPathFollower.CurrentPathIndex, 0, segmentCount - 1) + 1
                : 0;
            return $"ready={_multiPathData.IsReady}  segments={segmentCount}  segment={segmentIndex}/{segmentCount}\n"
                + $"global progress={_multiPathFollower.GlobalNormalizedTime:F2}\n"
                + $"local progress={_multiPathFollower.NormalizedTime:F2}  length={_multiPathData.PathLength:F2}m\n"
                + $"state={(_multiPathFollower.IsMoving ? "moving" : "paused/stopped")}";
        }

        private string GetQueueStatusText()
        {
            QueuedPathFollower leader = GetQueueLeader();
            float? distance = leader != null ? _queueManager.GetDistanceToAhead(leader) : null;
            string leaderState = leader == null
                ? "none"
                : leader.IsBlocked ? "BLOCKED" : leader.IsMoving ? "moving" : "stopped";
            string ahead = distance.HasValue ? $"{distance.Value:F2}m" : "none";
            float multiplier = leader == null ? 1f : leader.CurrentSpeedMultiplier;
            return $"visible={_queueVisible}  followers={_queueManager.FollowerCount}/{_queueFollowers.Length}\n"
                + $"spacing={_queueManager.DefaultSpacing:F2}m  leader={leaderState}\n"
                + $"ahead={ahead}  speed x{multiplier:F2}\n"
                + $"path length={_queuePathData.PathLength:F2}m  state={(_isPaused ? "paused" : "running")}";
        }

        private void StartNormalPath()
        {
            _pathFollower.PathData = _pathData;
            _pathFollower.CurrentMoveType = PathFollower.EMoveType.SpeedBased;
            _pathFollower.Speed = 3f;
            _pathFollower.Loop = true;
            if (!_pathFollower.IsMoving)
                _pathFollower.StartMove(_pathData);
        }

        private void StartMultiPath()
        {
            _multiPathFollower.MultiPathData = _multiPathData;
            _multiPathFollower.CurrentMoveType = PathFollower.EMoveType.SpeedBased;
            _multiPathFollower.Speed = 2.2f;
            _multiPathFollower.Loop = false;
            if (!_multiPathFollower.IsMoving)
                _multiPathFollower.StartMove(_multiPathData, RestartMultiPath, null);
        }

        private void StartQueueFollowers()
        {
            _queueManager.MultiPathData = _queuePathData;
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                QueuedPathFollower follower = _queueFollowers[i];
                if (follower == null || !follower.isActiveAndEnabled)
                    continue;

                follower.PathFollower.MultiPathData = _queuePathData;
                follower.PathFollower.CurrentMoveType = PathFollower.EMoveType.SpeedBased;
                follower.PathFollower.Speed = 1.8f - i * 0.15f;
                follower.PathFollower.Loop = false;
                follower.StartMove(_queuePathData, RestartQueueFollower);
            }

            ApplyQueueStartSpacing();
        }

        private void ReleaseQueueFollowers()
        {
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                QueuedPathFollower follower = _queueFollowers[i];
                if (follower == null || follower.PathFollower == null)
                    continue;

                ReleaseQueueFollower(follower);

                if (follower.PathFollower.IsInitialized && follower.PathFollower.IsMoving)
                    follower.PathFollower.StopMove();
            }
        }

        private void ResetQueueFollowerRegistrations()
        {
            // Release every registration before re-registering any follower. A
            // manager unregister uses a swap-remove, so releasing and initializing
            // one item at a time can leave Followers[0] pointing at the wrong
            // visual role until the next manager tick.
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                QueuedPathFollower follower = _queueFollowers[i];
                if (follower == null)
                    continue;

                ReleaseQueueFollower(follower);

                if (follower.PathFollower != null
                    && follower.PathFollower.IsInitialized
                    && follower.PathFollower.IsMoving)
                    follower.PathFollower.StopMove();
            }

            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                QueuedPathFollower follower = _queueFollowers[i];
                if (follower != null)
                    follower.Init();
            }
        }

        private static void ReleaseQueueFollower(QueuedPathFollower follower)
        {
            if (follower != null && (follower.IsInitialized || follower.IsFaulted))
                follower.Release();
        }

        private void ApplyQueueStartSpacing()
        {
            if (_queuePathData == null || !_queuePathData.IsReady || _queueManager == null)
                return;

            float pathLength = Mathf.Max(_queuePathData.PathLength, 0.001f);
            float gap = Mathf.Max(_queueManager.DefaultSpacing * 1.25f, 0.25f);
            float gapNormalized = Mathf.Clamp(gap / pathLength, 0.01f, 0.3f);

            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                QueuedPathFollower follower = _queueFollowers[i];
                if (follower == null || !follower.IsMoving)
                    continue;

                // Serialized order is only the visual role order: 0 is front, 2 is tail.
                float normalized = Mathf.Clamp01((_queueFollowers.Length - 1 - i) * gapNormalized);
                follower.PathFollower.SetGlobalNormalizedTime(normalized);
            }

            _queueManager.NotifySortNeeded();
        }

        private void RestartMultiPath()
        {
            if (!_isInitialized || _isFaulted || !_multiPathFollower.IsInitialized)
                return;
            if (!_multiPathFollower.IsMoving)
                _multiPathFollower.StartMove(_multiPathData, RestartMultiPath, null);
        }

        private void RestartQueueFollower()
        {
            if (!_isInitialized || _isFaulted || _isResettingQueue)
                return;

            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                QueuedPathFollower follower = _queueFollowers[i];
                if (follower != null && follower.isActiveAndEnabled && !follower.IsMoving)
                {
                    follower.PathFollower.MultiPathData = _queuePathData;
                    follower.PathFollower.Loop = false;
                    follower.StartMove(_queuePathData, RestartQueueFollower);
                    return;
                }
            }
        }

        private QueuedPathFollower GetQueueLeader()
        {
            if (_queueManager == null || _queueManager.FollowerCount == 0)
                return null;

            IReadOnlyList<QueuedPathFollower> registered = _queueManager.Followers;
            return registered.Count > 0 ? registered[0] : null;
        }

        private void SetQueueVisible(bool visible)
        {
            if (_queueVisible == visible)
                return;

            if (!visible && _activeLane == TransformPathShowcaseLane.Queue)
                PauseLane(TransformPathShowcaseLane.Queue);

            _queueVisible = visible;
            ApplyLanePresentation();
        }

        private void SetActiveLane(TransformPathShowcaseLane lane)
        {
            if (_activeLane != lane)
                PauseLane(_activeLane);

            _activeLane = lane;
            ApplyLanePresentation();
            FocusActiveLane();
        }

        private void PauseUnselectedLanes()
        {
            if (_activeLane != TransformPathShowcaseLane.Normal)
                PauseLane(TransformPathShowcaseLane.Normal);
            if (_activeLane != TransformPathShowcaseLane.MultiPath)
                PauseLane(TransformPathShowcaseLane.MultiPath);
            if (_activeLane != TransformPathShowcaseLane.Queue || !_queueVisible)
                PauseLane(TransformPathShowcaseLane.Queue);
        }

        private void PauseLane(TransformPathShowcaseLane lane)
        {
            switch (lane)
            {
                case TransformPathShowcaseLane.Normal:
                    if (!_isPaused)
                        _normalWasMovingWhenHidden = _pathFollower != null && _pathFollower.IsMoving;
                    if (_pathFollower != null && _pathFollower.IsInitialized)
                        _pathFollower.PauseMove();
                    break;
                case TransformPathShowcaseLane.MultiPath:
                    if (!_isPaused)
                        _multiWasMovingWhenHidden = _multiPathFollower != null && _multiPathFollower.IsMoving;
                    if (_multiPathFollower != null && _multiPathFollower.IsInitialized)
                        _multiPathFollower.PauseMove();
                    break;
                case TransformPathShowcaseLane.Queue:
                    // A queue hidden by the Queue Visibility toggle already has a
                    // snapshot. Do not overwrite it while switching to another lane.
                    if (_queueVisible && !_isPaused)
                    {
                        for (int i = 0; i < _queueFollowers.Length; i++)
                        {
                            QueuedPathFollower follower = _queueFollowers[i];
                            _queueWasMovingWhenHidden[i] = follower != null && follower.IsMoving;
                        }
                    }

                    for (int i = 0; i < _queueFollowers.Length; i++)
                    {
                        if (_queueFollowers[i] != null && _queueFollowers[i].IsInitialized)
                            _queueFollowers[i].PauseMove();
                    }
                    break;
            }
        }

        private void ApplyLanePresentation()
        {
            SetLaneRenderersVisible("Normal Path Lane", _activeLane == TransformPathShowcaseLane.Normal);
            SetLaneRenderersVisible("Multi Path Lane", _activeLane == TransformPathShowcaseLane.MultiPath);
            SetLaneRenderersVisible(
                "Queued Path Lane",
                _activeLane == TransformPathShowcaseLane.Queue && _queueVisible);

            if (!_isPaused)
                ResumeSelectedLaneFromVisibilitySnapshot();
        }

        private void ResumeSelectedLaneFromVisibilitySnapshot()
        {
            switch (_activeLane)
            {
                case TransformPathShowcaseLane.Normal:
                    if (_normalWasMovingWhenHidden)
                    {
                        _pathFollower.ResumeMove();
                        _normalWasMovingWhenHidden = false;
                    }
                    break;
                case TransformPathShowcaseLane.MultiPath:
                    if (_multiWasMovingWhenHidden)
                    {
                        _multiPathFollower.ResumeMove();
                        _multiWasMovingWhenHidden = false;
                    }
                    break;
                case TransformPathShowcaseLane.Queue:
                    if (!_queueVisible)
                        return;

                    for (int i = 0; i < _queueFollowers.Length; i++)
                    {
                        QueuedPathFollower follower = _queueFollowers[i];
                        if (follower != null && _queueWasMovingWhenHidden[i] && !follower.IsBlocked)
                            follower.ResumeMove();
                        _queueWasMovingWhenHidden[i] = false;
                    }
                    break;
            }
        }

        private void SetLaneRenderersVisible(string laneName, bool visible)
        {
            Transform lane = transform.Find(laneName);
            if (lane == null)
                return;

            Renderer[] renderers = lane.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = visible;
            }
        }

        private Bounds GetActiveLaneBounds()
        {
            string laneName = _activeLane == TransformPathShowcaseLane.Normal
                ? "Normal Path Lane"
                : _activeLane == TransformPathShowcaseLane.MultiPath
                    ? "Multi Path Lane"
                    : "Queued Path Lane";
            Transform lane = transform.Find(laneName);
            if (lane == null)
                return new Bounds(transform.position, new Vector3(14f, 1f, 10f));

            Renderer[] renderers = lane.GetComponentsInChildren<Renderer>(true);
            Bounds bounds = new Bounds(lane.position, new Vector3(14f, 1f, 10f));
            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return bounds;
        }

        private void ResolveFreeCamera()
        {
            if (_freeCamera != null)
                return;

            Camera mainCamera = Camera.main;
            if (mainCamera != null)
                _freeCamera = mainCamera.GetComponent<TransformPathFreeCamera>();

            if (_freeCamera == null)
                _freeCamera = FindFirstObjectByType<TransformPathFreeCamera>();
        }

        private void CreateQueueRoute()
        {
            if (_queuePathData != null)
                return;

            _queueRouteDataObject = new GameObject("Queued Path Route (Counter Style)");
            _queueRouteDataObject.transform.SetParent(GetLaneTransform("Queued Path Lane"), false);
            _queueRouteDataObject.SetActive(false);

            _queuePath = _queueRouteDataObject.AddComponent<PathData>();
            _queuePath.ConfigureControlPoints(new[]
            {
                new Vector3(-7f, 0.8f, -16f),
                new Vector3(-4f, 0.8f, -13f),
                new Vector3(0f, 0.8f, -15f),
                new Vector3(4f, 0.8f, -13f),
                new Vector3(7f, 0.8f, -16f),
            });

            _queuePathData = _queueRouteDataObject.AddComponent<MultiPathData>();
            _queuePathData.ConfigureSegments(new[] { _queuePath });
            _queueRouteDataObject.SetActive(true);

            if (!_queuePath.IsInitialized || !_queuePath.IsReady
                || !_queuePathData.IsInitialized || !_queuePathData.IsReady)
                throw new InvalidOperationException("Queue route providers failed to initialize.");

            _queueRouteLineObject = new GameObject("Queued Path Route Line");
            _queueRouteLineObject.transform.SetParent(GetLaneTransform("Queued Path Lane"), false);
            _queueRouteRenderer = _queueRouteLineObject.AddComponent<LineRenderer>();
            _queueRouteRenderer.useWorldSpace = true;
            _queueRouteRenderer.widthMultiplier = 0.09f;
            _queueRouteRenderer.startColor = new Color(1f, 0.7f, 0.15f, 1f);
            _queueRouteRenderer.endColor = new Color(1f, 0.35f, 0.1f, 1f);
            _queueRouteRenderer.positionCount = _queuePath.PathPoints.Length;
            _queueRouteRenderer.SetPositions(_queuePath.PathPoints);

            LineRenderer sourceRenderer = _pathData.GetComponent<LineRenderer>();
            if (sourceRenderer != null && sourceRenderer.sharedMaterial != null)
            {
                _queueRouteRenderer.sharedMaterial = sourceRenderer.sharedMaterial;
            }
            else
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    _queueRouteMaterial = new Material(shader);
                    _queueRouteRenderer.sharedMaterial = _queueRouteMaterial;
                }
            }
        }

        private void CreateMultiPathRouteLines()
        {
            if (_multiPathData == null || !_multiPathData.IsReady)
                return;

            LineRenderer sourceRenderer = _pathData != null ? _pathData.GetComponent<LineRenderer>() : null;
            Material sourceMaterial = sourceRenderer != null ? sourceRenderer.sharedMaterial : null;
            for (int i = 0; i < _multiPathData.PathDataConfigs.Count; i++)
            {
                MultiPathData.PathDataConfig config = _multiPathData.PathDataConfigs[i];
                if (config == null || config.PathData == null || !config.PathData.IsReady)
                    continue;

                GameObject lineObject = new GameObject("Multi Path Segment " + (char)('A' + i) + " Line");
                lineObject.transform.SetParent(GetLaneTransform("Multi Path Lane"), false);
                LineRenderer line = lineObject.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.widthMultiplier = 0.08f;
                Color color = i % 2 == 0
                    ? new Color(0.8f, 0.35f, 1f, 1f)
                    : new Color(1f, 0.35f, 0.55f, 1f);
                line.startColor = color;
                line.endColor = color;
                line.positionCount = config.PathData.PathPoints.Length;
                line.SetPositions(config.PathData.PathPoints);

                if (sourceMaterial != null)
                {
                    line.sharedMaterial = sourceMaterial;
                }
                else if (_multiRouteMaterial == null)
                {
                    Shader shader = Shader.Find("Sprites/Default");
                    if (shader != null)
                        _multiRouteMaterial = new Material(shader);
                    line.sharedMaterial = _multiRouteMaterial;
                }
                else
                {
                    line.sharedMaterial = _multiRouteMaterial;
                }

                _multiRouteRenderers.Add(line);
            }
        }

        private Transform GetLaneTransform(string laneName)
        {
            Transform lane = transform.Find(laneName);
            return lane != null ? lane : transform;
        }

        private void ValidateSerializedReferences()
        {
            if (_pathData == null || _pathFollower == null || _multiPathData == null || _multiPathFollower == null
                || _queueManager == null || _messageReceiver == null || _board == null)
                throw new InvalidOperationException("TransformPath overview requires all serialized references.");
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("TransformPathOverviewController has not been initialized.");

            if (_isInitialized)
            {
                if (_pathFollower != null && _pathFollower.IsMoving)
                    _pathFollower.StopMove();
                if (_multiPathFollower != null && _multiPathFollower.IsMoving)
                    _multiPathFollower.StopMove();
                if (_queueFollowers != null)
                {
                    _isResettingQueue = true;
                    try
                    {
                        ReleaseQueueFollowers();
                    }
                    finally
                    {
                        _isResettingQueue = false;
                    }
                }
            }

            for (int i = 0; i < _multiRouteRenderers.Count; i++)
            {
                if (_multiRouteRenderers[i] != null)
                    Destroy(_multiRouteRenderers[i].gameObject);
            }
            _multiRouteRenderers.Clear();

            if (_queueRouteLineObject != null)
                Destroy(_queueRouteLineObject);
            if (_queueRouteMaterial != null)
                Destroy(_queueRouteMaterial);
            if (_multiRouteMaterial != null)
                Destroy(_multiRouteMaterial);
            if (_queueRouteDataObject != null)
                Destroy(_queueRouteDataObject);

            _queueRouteLineObject = null;
            _queueRouteRenderer = null;
            _queueRouteMaterial = null;
            _multiRouteMaterial = null;
            _queuePathData = null;
            _queuePath = null;
            _queueRouteDataObject = null;

            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
            _isPaused = false;
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

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
