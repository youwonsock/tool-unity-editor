using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.TransformPath.Samples
{
    public enum ETransformPathShowcaseLane
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
        #region Member Variables

        [Header("Normal Path Lane")]
        [SerializeField] private PathData _pathData;
        [SerializeField] private PathFollower _pathFollower;

        [Header("Multi Path Lane")]
        [SerializeField] private MultiPathData _multiPathData;
        [SerializeField] private PathFollower _multiPathFollower;

        [Header("Queued Path Lane")]
        [SerializeField] private QueuedPathManager _queueManager;
        [SerializeField] private MultiPathData _queuePathData;
        [SerializeField] private PathQueueMember[] _queueFollowers;

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
        private Action[] _queueRestarts;
        private int _presentationMode;
        private ETransformPathShowcaseLane _activeLane = ETransformPathShowcaseLane.Normal;

        #endregion


        #region Properties

        public bool IsInitialized => _isInitialized;
        public bool IsPaused => _isPaused;
        public int PresentationMode => _presentationMode;
        public bool QueueVisible => _queueVisible;
        public ETransformPathShowcaseLane ActiveLane => _activeLane;
        public MultiPathData QueuePathData => _queuePathData;
        public EPathCurveType CurrentCurveType => _pathData == null ? EPathCurveType.Linear : _pathData.CurveType;

        #endregion


        #region Unity Events

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
            _pathFollower?.Stop();
            _multiPathFollower?.Stop();
            if (_queueFollowers != null)
            {
                for (int i = 0; i < _queueFollowers.Length; i++)
                    _queueFollowers[i]?.Follower.Stop();
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

        private void OnDestroy()
        {
            Release();
        }

        #endregion


        #region Public Methods

        public void ShowNormalLane() => SetActiveLane(ETransformPathShowcaseLane.Normal);
        public void ShowMultiPathLane() => SetActiveLane(ETransformPathShowcaseLane.MultiPath);
        public void ShowQueuedLane() => SetActiveLane(ETransformPathShowcaseLane.Queue);

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

            float progress = _pathFollower != null ? _pathFollower.Progress : 0f;
            EPathCurveType curve = mode == 0
                ? EPathCurveType.Linear
                : mode == 1
                    ? EPathCurveType.SplineInterpolating
                    : EPathCurveType.SplineApproximating;
            _pathData.CurveType = curve;
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
                _queueWasMovingWhenHidden[i] = _queueFollowers[i] != null && _queueFollowers[i].Follower.IsMoving;

            _pathFollower?.Pause();
            _multiPathFollower?.Pause();
            for (int i = 0; i < _queueFollowers.Length; i++)
                _queueFollowers[i]?.Follower.Pause();
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
            _pathFollower?.Stop();
            _multiPathFollower?.Stop();
            for (int i = 0; i < _queueFollowers.Length; i++)
                _queueFollowers[i]?.Follower.Stop();

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
            if (_activeLane == ETransformPathShowcaseLane.Normal && _pathFollower != null)
                _pathFollower.Seek(Mathf.Clamp01(_pathFollower.Progress + delta));
            else if (_activeLane == ETransformPathShowcaseLane.MultiPath && _multiPathFollower != null)
                _multiPathFollower.Seek(Mathf.Clamp01(_multiPathFollower.Progress + delta));
        }

        public void BlockQueueLeader()
        {
            PathQueueMember leader = GetQueueLeader();
            leader?.Hold();
        }

        public void UnblockQueueLeader()
        {
            PathQueueMember leader = GetQueueLeader();
            leader?.ReleaseHold();
        }

        public void ToggleQueueVisibility()
        {
            if (!_isInitialized)
                return;
            _queueVisible = !_queueVisible;
            if (_activeLane == ETransformPathShowcaseLane.Queue)
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
                case ETransformPathShowcaseLane.MultiPath:
                    return GetMultiStatus();
                case ETransformPathShowcaseLane.Queue:
                    return GetQueueStatus();
                default:
                    return GetNormalStatus();
            }
        }

        #endregion


        #region Private Methods

        private string GetNormalStatus()
        {
            return $"NORMAL  ready={_pathData.IsReady}  curve={_pathData.CurveType}\n"
                + $"length={_pathData.PathLength:F2}m  progress={_pathFollower.Progress:F2}  "
                + $"{DescribeMovement(_pathFollower)}\n"
                + $"samples={_pathData.SamplePointCount}  state={_pathFollower.State}\n"
                + $"events={DescribeEvents(_pathData, _pathFollower.MoveType)}";
        }

        private string GetMultiStatus()
        {
            int count = _multiPathData.IsReady ? _multiPathData.SegmentCount : 0;
            int index = count == 0 ? 0 : Mathf.Clamp(_multiPathFollower.SegmentIndex + 1, 1, count);
            return $"MULTI  ready={_multiPathData.IsReady}  segment={index}/{count}\n"
                + $"global={_multiPathFollower.Progress:F2}  local={_multiPathFollower.SegmentProgress:F2}  "
                + $"{DescribeMovement(_multiPathFollower)}\n"
                + $"length={_multiPathData.PathLength:F2}m  state={_multiPathFollower.State}\n"
                + $"events={DescribeSequenceEvents(_multiPathData)}";
        }

        private string GetQueueStatus()
        {
            PathQueueMember leader = GetQueueLeader();
            string leaderText = leader == null
                ? "none"
                : leader.IsBlocked ? "BLOCKED" : leader.Follower.IsMoving ? "moving" : "stopped";
            string spacing = _queueManager == null ? "-" : $"{_queueManager.DefaultSpacing:F2}m";
            string ahead = "none";
            string multiplier = "1.00";
            if (leader != null && _queueManager.TryGetMemberStatus(leader, out PathQueueStatus state))
            {
                ahead = state.DistanceToAhead.HasValue ? $"{state.DistanceToAhead.Value:F2}m" : "none";
                multiplier = leader.CurrentSpeedMultiplier.ToString("F2");
            }
            return $"QUEUE  agents={_queueManager.MemberCount}/{_queueFollowers.Length}  visible={_queueVisible}\n"
                + $"spacing={spacing}  leader={leaderText}  ahead={ahead}\n"
                + $"speed x{multiplier}  routeRev={_queueManager.RouteRevision}\n"
                + $"length={_queuePathData.PathLength:F2}m  state={(_isPaused ? "paused" : "running")}  "
                + $"{(leader == null ? "movement=none" : DescribeMovement(leader.Follower))}\n"
                + $"events={DescribeSequenceEvents(_queuePathData)}";
        }

        private static string DescribeMovement(PathFollower playback)
        {
            if (playback == null)
                return "movement=none";
            return playback.MoveType == EPathMoveType.SpeedBased
                ? $"speed={playback.Speed:F2}"
                : $"duration={playback.Duration:F2}s";
        }

        private static string DescribeEvents(
            PathData path,
            EPathMoveType moveType)
        {
            if (path == null || path.Events.Count == 0)
                return "none";

            List<string> descriptions = new List<string>(path.Events.Count);
            for (int i = 0; i < path.Events.Count; i++)
                descriptions.Add(DescribeEvent(path.Events[i], moveType));
            return string.Join(" · ", descriptions);
        }

        private static string DescribeSequenceEvents(MultiPathData sequence)
        {
            if (sequence == null || !sequence.IsReady)
                return "none";

            List<string> descriptions = new List<string>();
            for (int segment = 0; segment < sequence.SegmentCount; segment++)
            {
                PathData path = sequence.GetSegmentPath(segment);
                EPathMoveType moveType = sequence.GetSegmentMovement(segment).MoveType;
                for (int i = 0; i < path.Events.Count; i++)
                    descriptions.Add($"S{segment + 1} {DescribeEvent(path.Events[i], moveType)}");
            }
            return descriptions.Count == 0
                ? "none"
                : string.Join(" · ", descriptions);
        }

        private static string DescribeEvent(
            PathRuntimeEvent pathEvent,
            EPathMoveType moveType)
        {
            PathEventDefinition definition = pathEvent.Definition;
            if (definition == null)
                return $"event @ {pathEvent.NormalizedTime:F2}";

            string action = "notify";
            PathValueChange change = moveType == EPathMoveType.SpeedBased
                ? definition.SpeedChange
                : definition.DurationChange;
            if (definition.Lifecycle == EPathEventLifecycle.Pause)
                action = "Pause";
            else if (change.Enabled)
                action = moveType == EPathMoveType.SpeedBased
                    ? $"Speed {change.Target:F2}"
                    : $"Duration {change.Target:F2}s";

            string delayed = definition.DelayedEvents.Count == 0
                ? string.Empty
                : $" + delay {definition.DelayedEvents[0].Delay:F2}s";
            return $"{definition.EventName} @ {pathEvent.NormalizedTime:F2} ({action}{delayed})";
        }

        private void StartNormalPath()
        {
            if (_pathFollower != null && !_pathFollower.IsMoving)
                _pathFollower.Play(PathPlaybackRequest.Single(_pathData, loop: true));
        }

        private void StartMultiPath()
        {
            if (_multiPathFollower != null && !_multiPathFollower.IsMoving)
                _multiPathFollower.Play(PathPlaybackRequest.Sequence(_multiPathData, loop: false));
        }

        private void StartQueueFollowers()
        {
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                PathQueueMember follower = _queueFollowers[i];
                if (follower == null || !follower.isActiveAndEnabled || follower.Follower.IsMoving)
                    continue;
                follower.StartQueuedPlayback(PathPlaybackRequest.Sequence(_queuePathData, loop: false));
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
                PathQueueMember follower = _queueFollowers[i];
                if (follower != null && follower.Follower.IsMoving)
                    follower.Follower.Seek(Mathf.Clamp01((_queueFollowers.Length - 1 - i) * normalizedGap));
            }
        }

        private void SubscribeCompletionEvents()
        {
            if (_completionEventsSubscribed)
                return;
            _multiPathFollower.Completed += RestartMultiPath;
            _queueRestarts = new Action[_queueFollowers.Length];
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                PathQueueMember member = _queueFollowers[i];
                if (member == null)
                    continue;
                _queueRestarts[i] = () => RestartQueueFollower(member);
                member.Follower.Completed += _queueRestarts[i];
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
                    if (_queueFollowers[i] != null && _queueRestarts != null && _queueRestarts[i] != null)
                        _queueFollowers[i].Follower.Completed -= _queueRestarts[i];
                }
            }
            _completionEventsSubscribed = false;
        }

        private void RestartMultiPath()
        {
            if (_isInitialized && !_isPaused)
                StartMultiPath();
        }

        private void RestartQueueFollower(PathQueueMember follower)
        {
            if (!_isInitialized || _isPaused || follower == null || !follower.isActiveAndEnabled)
                return;
            follower.StartQueuedPlayback(PathPlaybackRequest.Sequence(_queuePathData, loop: false));
        }

        private PathQueueMember GetQueueLeader()
        {
            if (_queueManager == null || _queueManager.MemberCount == 0)
                return null;
            return _queueManager.GetMember(0);
        }

        private void SetActiveLane(ETransformPathShowcaseLane lane)
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
            if (_activeLane != ETransformPathShowcaseLane.Normal)
                PauseLane(ETransformPathShowcaseLane.Normal);
            if (_activeLane != ETransformPathShowcaseLane.MultiPath)
                PauseLane(ETransformPathShowcaseLane.MultiPath);
            if (_activeLane != ETransformPathShowcaseLane.Queue || !_queueVisible)
                PauseLane(ETransformPathShowcaseLane.Queue);
        }

        private void PauseLane(ETransformPathShowcaseLane lane)
        {
            switch (lane)
            {
                case ETransformPathShowcaseLane.Normal:
                    _normalWasMovingWhenHidden = _pathFollower != null && _pathFollower.IsMoving;
                    _pathFollower?.Pause();
                    break;
                case ETransformPathShowcaseLane.MultiPath:
                    _multiWasMovingWhenHidden = _multiPathFollower != null && _multiPathFollower.IsMoving;
                    _multiPathFollower?.Pause();
                    break;
                case ETransformPathShowcaseLane.Queue:
                    for (int i = 0; i < _queueFollowers.Length; i++)
                    {
                        _queueWasMovingWhenHidden[i] = _queueFollowers[i] != null && _queueFollowers[i].Follower.IsMoving;
                        _queueFollowers[i]?.Follower.Pause();
                    }
                    break;
            }
        }

        private void ResumeSelectedLaneFromVisibilitySnapshot()
        {
            if (_activeLane == ETransformPathShowcaseLane.Normal && _normalWasMovingWhenHidden)
                _pathFollower.Resume();
            else if (_activeLane == ETransformPathShowcaseLane.MultiPath && _multiWasMovingWhenHidden)
                _multiPathFollower.Resume();
            else if (_activeLane == ETransformPathShowcaseLane.Queue && _queueVisible)
                ResumeQueueFromVisibilitySnapshot();
        }

        private void PauseQueueForVisibility()
        {
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                _queueWasMovingWhenHidden[i] = _queueFollowers[i] != null && _queueFollowers[i].Follower.IsMoving;
                _queueFollowers[i]?.Follower.Pause();
            }
        }

        private void ResumeQueueFromVisibilitySnapshot()
        {
            if (!_queueVisible)
                return;
            for (int i = 0; i < _queueFollowers.Length; i++)
            {
                if (_queueFollowers[i] != null && _queueWasMovingWhenHidden[i])
                    _queueFollowers[i].Follower.Resume();
                _queueWasMovingWhenHidden[i] = false;
            }
        }

        private void ApplyLanePresentation()
        {
            SetLaneRenderersVisible("Normal Path Lane", _activeLane == ETransformPathShowcaseLane.Normal);
            SetLaneRenderersVisible("Multi Path Lane", _activeLane == ETransformPathShowcaseLane.MultiPath);
            SetLaneRenderersVisible("Queued Path Lane", _activeLane == ETransformPathShowcaseLane.Queue && _queueVisible);
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
            string laneName = _activeLane == ETransformPathShowcaseLane.Normal
                ? "Normal Path Lane"
                : _activeLane == ETransformPathShowcaseLane.MultiPath ? "Multi Path Lane" : "Queued Path Lane";
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
                if (!hasBounds)
                {
                    bounds = renderers[i].bounds;
                    hasBounds = true;
                }
                else
                    bounds.Encapsulate(renderers[i].bounds);
            }
            return bounds;
        }

        private void ResolveFreeCamera()
        {
            if (_freeCamera != null)
                return;
            Camera main = Camera.main;
            if (main != null && main.TryGetComponent(out _freeCamera))
                return;
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
                PathData path = _multiPathData.GetSegmentPath(i);
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

        #endregion
    }
}
