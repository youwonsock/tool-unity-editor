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
    /// Small, deterministic sample orchestrator for the three canonical APIs:
    /// aggregate single-provider playback, sequence playback, and queue playback.
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
        [SerializeField] private MultiPathData _queuePathData;
        [SerializeField] private QueuedPathFollower[] _queueFollowers;

        [Header("Presentation")]
        [SerializeField] private TransformPathFreeCamera _freeCamera;
        [SerializeField] private TransformPathOverviewBoard _board;

        private readonly List<LineRenderer> _multiRouteRenderers = new List<LineRenderer>();
        private Material _multiRouteMaterial;
        private bool _isInitialized;
        private bool _isPaused;
        private bool _queueVisible = true;
        private bool _normalWasMovingWhenHidden;
        private bool _multiWasMovingWhenHidden;
        private bool[] _queueWasMovingWhenHidden;
        private bool _completionEventsSubscribed;
        private int _presentationMode;
        private TransformPathShowcaseLane _activeLane = TransformPathShowcaseLane.Normal;

        public bool IsInitialized => _isInitialized;
        public bool IsPaused => _isPaused;
        public int PresentationMode => _presentationMode;
        public bool QueueVisible => _queueVisible;
        public TransformPathShowcaseLane ActiveLane => _activeLane;
        public MultiPathData QueuePathData => _queuePathData;
        public PathData.ECurveType CurrentCurveType => _pathData == null ? PathData.ECurveType.Linear : _pathData.CurveType;

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        private void Start()
        {
            if (!_isInitialized)
                return;
            StartNormalPath();
            StartMultiPath();
            StartQueueFollowers();
            PauseUnselectedLanes();
            ApplyLanePresentation();
            FocusActiveLane();
        }

        private void Update()
        {
            if (!_isInitialized)
                return;

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

            _board?.Render(GetActiveStatusText());
        }

        public void Init()
        {
            if (_isInitialized)
                return;
            if (_pathData == null || _pathFollower == null || _multiPathData == null || _multiPathFollower == null
                || _queueManager == null || _queueFollowers == null || _queueFollowers.Length == 0)
            {
                Debug.LogError("TransformPath overview is missing a required serialized reference.", this);
                return;
            }

            _queueWasMovingWhenHidden = new bool[_queueFollowers.Length];

            if (!_pathData.IsReady || !_multiPathData.IsReady || _queuePathData == null || !_queuePathData.IsReady)
            {
                Debug.LogError("TransformPath overview requires all providers to be ready.", this);
                return;
            }

            _queueManager.ConfigureRoute(_queuePathData);
            ResolveFreeCamera();
            CreateMultiPathRouteLines();
            SubscribeCompletionEvents();
            _isInitialized = true;
        }

        public void Release()
        {
            if (!_isInitialized)
                return;

            UnsubscribeCompletionEvents();
            _pathFollower?.StopMove();
            _multiPathFollower?.StopMove();
            if (_queueFollowers != null)
            {
                for (int i = 0; i < _queueFollowers.Length; i++)
                    _queueFollowers[i]?.StopMove();
            }

            for (int i = 0; i < _multiRouteRenderers.Count; i++)
            {
                if (_multiRouteRenderers[i] != null)
                    Destroy(_multiRouteRenderers[i].gameObject);
            }
            _multiRouteRenderers.Clear();
            if (_multiRouteMaterial != null)
                Destroy(_multiRouteMaterial);

            _isInitialized = false;
        }

        private void OnDestroy()
        {
            Release();
        }

        public void ShowNormalLane() => SetActiveLane(TransformPathShowcaseLane.Normal);
        public void ShowMultiPathLane() => SetActiveLane(TransformPathShowcaseLane.MultiPath);
        public void ShowQueuedLane() => SetActiveLane(TransformPathShowcaseLane.Queue);

        public void FocusActiveLane()
        {
            if (!_isInitialized || _freeCamera == null)
                return;
            _freeCamera.FocusOnBounds(GetActiveLaneBounds());
        }

        /// <summary>Chooses one of the three curve presets for the Normal lane.</summary>
        public void SelectPresentationMode(int mode)
        {
            if (!_isInitialized || mode < 0 || mode > 2 || _pathData == null)
                return;

            float progress = _pathFollower != null ? _pathFollower.NormalizedTime : 0f;
            PathData.ECurveType curve = mode == 0
                ? PathData.ECurveType.Linear
                : mode == 1
                    ? PathData.ECurveType.SplineInterpolating
                    : PathData.ECurveType.SplineApproximating;
            _pathData.SetCurveType(curve);
            if (_pathFollower != null && ReferenceEquals(_pathFollower.CurrentProvider, _pathData))
                _pathFollower.Seek(progress);
            _presentationMode = mode;
        }

        public void TogglePauseAll()
        {
            if (_isPaused)
                ResumeAll();
            else
                PauseAll();
        }

        public void PauseAll()
        {
            if (!_isInitialized || _isPaused)
                return;

            _normalWasMovingWhenHidden = _pathFollower != null && _pathFollower.IsMoving;
            _multiWasMovingWhenHidden = _multiPathFollower != null && _multiPathFollower.IsMoving;
            for (int i = 0; i < _queueWasMovingWhenHidden.Length; i++)
                _queueWasMovingWhenHidden[i] = _queueFollowers[i] != null && _queueFollowers[i].IsMoving;

            _pathFollower?.PauseMove();
            _multiPathFollower?.PauseMove();
            for (int i = 0; i < _queueFollowers.Length; i++)
                _queueFollowers[i]?.PauseMove();
            _isPaused = true;
        }

        public void ResumeAll()
        {
            if (!_isInitialized || !_isPaused)
                return;
            _isPaused = false;
            ResumeSelectedLaneFromVisibilitySnapshot();
            _normalWasMovingWhenHidden = false;
            _multiWasMovingWhenHidden = false;
            if (_queueWasMovingWhenHidden != null)
                Array.Clear(_queueWasMovingWhenHidden, 0, _queueWasMovingWhenHidden.Length);
        }

        public void ResetAll()
        {
            if (!_isInitialized)
                return;
            bool wasPaused = _isPaused;
            _isPaused = false;
            _pathFollower?.StopMove();
            _multiPathFollower?.StopMove();
            for (int i = 0; i < _queueFollowers.Length; i++)
                _queueFollowers[i]?.StopMove();

            StartNormalPath();
            StartMultiPath();
            StartQueueFollowers();
            PauseUnselectedLanes();
            ApplyLanePresentation();
            if (wasPaused)
                PauseAll();
        }

        public void SeekBackward() => SeekSelected(-0.1f);
        public void SeekForward() => SeekSelected(0.1f);

        public void SeekSelected(float delta)
        {
            if (!_isInitialized || float.IsNaN(delta) || float.IsInfinity(delta))
                return;
            if (_activeLane == TransformPathShowcaseLane.Normal && _pathFollower != null)
                _pathFollower.Seek(Mathf.Clamp01(_pathFollower.NormalizedTime + delta));
            else if (_activeLane == TransformPathShowcaseLane.MultiPath && _multiPathFollower != null)
                _multiPathFollower.Seek(Mathf.Clamp01(_multiPathFollower.GlobalNormalizedTime + delta));
        }

        public void BlockQueueLeader()
        {
            QueuedPathFollower leader = GetQueueLeader();
            leader?.ForceBlock();
        }

        public void UnblockQueueLeader()
        {
            QueuedPathFollower leader = GetQueueLeader();
            leader?.ForceUnblock();
        }

        public void ToggleQueueVisibility()
        {
            if (!_isInitialized)
                return;
            _queueVisible = !_queueVisible;
            if (_activeLane == TransformPathShowcaseLane.Queue)
            {
                if (_queueVisible)
                    ResumeQueueFromVisibilitySnapshot();
                else
                    PauseQueueForVisibility();
            }
            ApplyLanePresentation();
        }

        public string GetActiveStatusText()
        {
            if (!_isInitialized)
                return "TransformPath · initializing";
            switch (_activeLane)
            {
                case TransformPathShowcaseLane.MultiPath:
                    return GetMultiStatus();
                case TransformPathShowcaseLane.Queue:
                    return GetQueueStatus();
                default:
                    return GetNormalStatus();
            }
        }

        private string GetNormalStatus()
        {
            return $"NORMAL  ready={_pathData.IsReady}  curve={_pathData.CurveType}\n"
                + $"length={_pathData.PathLength:F2}m  progress={_pathFollower.NormalizedTime:F2}\n"
                + $"samples={_pathData.SamplePointCount}  state={_pathFollower.State}\n"
                + "events=Pause @ 0.25 (0.5s) · SlowDown @ 0.50 (1.5) · Accel @ 0.75 (6.0)";
        }

        private string GetMultiStatus()
        {
            int count = _multiPathData.IsReady ? _multiPathData.SegmentCount : 0;
            int index = count == 0 ? 0 : Mathf.Clamp(_multiPathFollower.CurrentSegmentIndex + 1, 1, count);
            return $"MULTI  ready={_multiPathData.IsReady}  segment={index}/{count}\n"
                + $"global={_multiPathFollower.GlobalNormalizedTime:F2}  local={_multiPathFollower.NormalizedTime:F2}\n"
                + $"length={_multiPathData.PathLength:F2}m  state={_multiPathFollower.State}\n"
                + "events=Pause @ 0.25 (0.5s) · SlowDown @ 0.50 (dur 2.0) · Accel @ 0.75 (dur 0.5)";
        }

        private string GetQueueStatus()
        {
            QueuedPathFollower leader = GetQueueLeader();
            string leaderText = leader == null
                ? "none"
                : leader.IsBlocked ? "BLOCKED" : leader.IsMoving ? "moving" : "stopped";
            string spacing = _queueManager == null ? "-" : $"{_queueManager.DefaultSpacing:F2}m";
            string ahead = "none";
            string multiplier = "1.00";
            if (leader != null && _queueManager.TryGetState(leader, out PathQueueState state))
            {
                ahead = state.DistanceToAhead.HasValue ? $"{state.DistanceToAhead.Value:F2}m" : "none";
                multiplier = leader.CurrentSpeedMultiplier.ToString("F2");
            }
            return $"QUEUE  agents={_queueManager.AgentCount}/{_queueFollowers.Length}  visible={_queueVisible}\n"
                + $"spacing={spacing}  leader={leaderText}  ahead={ahead}\n"
                + $"speed x{multiplier}  routeRev={_queueManager.RouteRevision}\n"
                + $"length={_queuePathData.PathLength:F2}m  state={(_isPaused ? "paused" : "running")}\n"
                + "events=Pause @ 0.25 (0.5s) · SlowDown @ 0.50 (0.5) · Accel @ 0.75 (2.0)";
        }

        private void StartNormalPath()
        {
            if (_pathFollower != null && !_pathFollower.IsMoving)
                _pathFollower.StartMove(_pathData, new PathMoveSettings(EPathMoveType.SpeedBased, 3f, loop: true));
        }

        private void StartMultiPath()
        {
            if (_multiPathFollower != null && !_multiPathFollower.IsMoving)
                _multiPathFollower.StartSequence(_multiPathData, new PathSequenceSettings(loop: false, preserveSpeedBetweenSegments: true));
        }

        private void StartQueueFollowers()
        {
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                QueuedPathFollower follower = _queueFollowers[i];
                if (follower == null || !follower.isActiveAndEnabled || follower.IsMoving)
                    continue;
                follower.StartSequence(_queuePathData, new PathSequenceSettings(loop: false, preserveSpeedBetweenSegments: true));
            }
            ApplyQueueStartSpacing();
        }

        private void ApplyQueueStartSpacing()
        {
            if (_queuePathData == null || !_queuePathData.IsReady)
                return;
            float normalizedGap = Mathf.Clamp((_queueManager.DefaultSpacing * 1.5f) / _queuePathData.PathLength, 0.01f, 0.3f);
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                QueuedPathFollower follower = _queueFollowers[i];
                if (follower != null && follower.IsMoving)
                    follower.PathFollower.Seek(Mathf.Clamp01((_queueFollowers.Length - 1 - i) * normalizedGap));
            }
        }

        private void SubscribeCompletionEvents()
        {
            if (_completionEventsSubscribed)
                return;
            _multiPathFollower.Completed += RestartMultiPath;
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                if (_queueFollowers[i] != null)
                    _queueFollowers[i].OnCompleted += RestartQueueFollower;
            }
            _completionEventsSubscribed = true;
        }

        private void UnsubscribeCompletionEvents()
        {
            if (!_completionEventsSubscribed)
                return;
            if (_multiPathFollower != null)
                _multiPathFollower.Completed -= RestartMultiPath;
            if (_queueFollowers != null)
            {
                for (int i = 0; i < _queueFollowers.Length; i++)
                {
                    if (_queueFollowers[i] != null)
                        _queueFollowers[i].OnCompleted -= RestartQueueFollower;
                }
            }
            _completionEventsSubscribed = false;
        }

        private void RestartMultiPath()
        {
            if (_isInitialized && !_isPaused)
                StartMultiPath();
        }

        private void RestartQueueFollower(QueuedPathFollower follower)
        {
            if (!_isInitialized || _isPaused || follower == null || !follower.isActiveAndEnabled)
                return;
            follower.StartSequence(_queuePathData, new PathSequenceSettings(loop: false, preserveSpeedBetweenSegments: true));
        }

        private QueuedPathFollower GetQueueLeader()
        {
            if (_queueManager == null || _queueManager.AgentCount == 0)
                return null;
            return _queueManager.GetAgent(0) as QueuedPathFollower;
        }

        private void SetActiveLane(TransformPathShowcaseLane lane)
        {
            if (!_isInitialized || _activeLane == lane)
                return;
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
                    _normalWasMovingWhenHidden = _pathFollower != null && _pathFollower.IsMoving;
                    _pathFollower?.PauseMove();
                    break;
                case TransformPathShowcaseLane.MultiPath:
                    _multiWasMovingWhenHidden = _multiPathFollower != null && _multiPathFollower.IsMoving;
                    _multiPathFollower?.PauseMove();
                    break;
                case TransformPathShowcaseLane.Queue:
                    for (int i = 0; i < _queueFollowers.Length; i++)
                    {
                        _queueWasMovingWhenHidden[i] = _queueFollowers[i] != null && _queueFollowers[i].IsMoving;
                        _queueFollowers[i]?.PauseMove();
                    }
                    break;
            }
        }

        private void ResumeSelectedLaneFromVisibilitySnapshot()
        {
            if (_activeLane == TransformPathShowcaseLane.Normal && _normalWasMovingWhenHidden)
                _pathFollower.ResumeMove();
            else if (_activeLane == TransformPathShowcaseLane.MultiPath && _multiWasMovingWhenHidden)
                _multiPathFollower.ResumeMove();
            else if (_activeLane == TransformPathShowcaseLane.Queue && _queueVisible)
                ResumeQueueFromVisibilitySnapshot();
        }

        private void PauseQueueForVisibility()
        {
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                _queueWasMovingWhenHidden[i] = _queueFollowers[i] != null && _queueFollowers[i].IsMoving;
                _queueFollowers[i]?.PauseMove();
            }
        }

        private void ResumeQueueFromVisibilitySnapshot()
        {
            if (!_queueVisible)
                return;
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                if (_queueFollowers[i] != null && _queueWasMovingWhenHidden[i])
                    _queueFollowers[i].ResumeMove();
                _queueWasMovingWhenHidden[i] = false;
            }
        }

        private void ApplyLanePresentation()
        {
            SetLaneRenderersVisible("Normal Path Lane", _activeLane == TransformPathShowcaseLane.Normal);
            SetLaneRenderersVisible("Multi Path Lane", _activeLane == TransformPathShowcaseLane.MultiPath);
            SetLaneRenderersVisible("Queued Path Lane", _activeLane == TransformPathShowcaseLane.Queue && _queueVisible);
            if (!_isPaused)
                ResumeSelectedLaneFromVisibilitySnapshot();
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
                : _activeLane == TransformPathShowcaseLane.MultiPath ? "Multi Path Lane" : "Queued Path Lane";
            Transform lane = transform.Find(laneName);
            if (lane == null)
                return new Bounds(transform.position, Vector3.one * 10f);
            Renderer[] renderers = lane.GetComponentsInChildren<Renderer>(true);
            Bounds bounds = new Bounds(lane.position, Vector3.one * 10f);
            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null)
                    continue;
                if (!hasBounds) { bounds = renderers[i].bounds; hasBounds = true; }
                else bounds.Encapsulate(renderers[i].bounds);
            }
            return bounds;
        }

        private void ResolveFreeCamera()
        {
            if (_freeCamera != null)
                return;
            Camera main = Camera.main;
            if (main != null)
                _freeCamera = main.GetComponent<TransformPathFreeCamera>();
            if (_freeCamera == null)
                _freeCamera = FindFirstObjectByType<TransformPathFreeCamera>();
        }

        private void CreateMultiPathRouteLines()
        {
            if (!_multiPathData.IsReady)
                return;
            Transform parent = transform.Find("Multi Path Lane");
            if (parent == null)
                parent = transform;
            for (int i = 0; i < _multiPathData.SegmentCount; i++)
            {
                PathData path = _multiPathData.GetSegmentConfig(i).PathData;
                if (path == null || !path.IsReady)
                    continue;
                GameObject lineObject = new GameObject($"Multi Path Segment {i + 1} Line");
                lineObject.transform.SetParent(parent, false);
                LineRenderer line = lineObject.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.widthMultiplier = 0.08f;
                Color color = i % 2 == 0 ? new Color(0.8f, 0.35f, 1f) : new Color(1f, 0.35f, 0.55f);
                line.startColor = color;
                line.endColor = color;
                line.positionCount = path.SamplePointCount;
                for (int point = 0; point < line.positionCount; point++)
                    line.SetPosition(point, path.GetSamplePoint(point));
                _multiRouteRenderers.Add(line);
            }
        }
    }
}
