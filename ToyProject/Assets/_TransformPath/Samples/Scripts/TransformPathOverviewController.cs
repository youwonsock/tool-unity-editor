using UnityEngine;

namespace Common.TransformPath.Samples
{
    public enum ETransformPathShowcaseLane
    {
        Normal = 0,
        MultiPath = 1,
        Queue = 2,
        Shape = 3,
    }

    /// <summary>
    /// Sample orchestrator for the four lanes: a single path, a sequence, a
    /// queue and a shape. It owns the lane switching and the UI commands; the
    /// queue lane, the status text and the rendering live in their own classes.
    /// The shape lane's follower starts itself through its Play On Start setting.
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

        [Header("Shape Path Lane")]
        [SerializeField] private TransformPathCircleShape _circleShape;
        [SerializeField] private PathFollower _shapeFollower;

        [Header("Presentation")]
        [SerializeField] private TransformPathFreeCamera _freeCamera;
        [SerializeField] private TransformPathOverviewBoard _board;
        [Tooltip("Unlit URP material for the route lines the sample draws at runtime. Each line sets its own _BaseColor.")]
        [SerializeField] private Material _routeLineMaterial;

        private TransformPathQueueLane _queue;
        private TransformPathLanePresentation _presentation;
        private bool _isInitialized;
        private bool _isPaused;
        private bool _queueVisible = true;
        private bool _normalWasMovingWhenHidden;
        private bool _multiWasMovingWhenHidden;
        private bool _shapeWasMovingWhenHidden;
        private bool _completionEventsSubscribed;
        private ETransformPathShowcaseLane _activeLane = ETransformPathShowcaseLane.Normal;

        #endregion


        #region Properties

        public bool IsPaused => _isPaused;
        public bool QueueVisible => _queueVisible;
        public ETransformPathShowcaseLane ActiveLane => _activeLane;

        #endregion


        #region Unity Events

        public void Init()
        {
            if (_isInitialized)
                return;
            if (_pathData == null || _pathFollower == null || _multiPathData == null || _multiPathFollower == null
                || _queueManager == null || _queueFollowers == null || _queueFollowers.Length == 0
                || _circleShape == null || _shapeFollower == null || _routeLineMaterial == null)
            {
                Debug.LogError("TransformPath overview is missing a required serialized reference.", this);
                return;
            }
            if (!_pathData.IsReady || !_multiPathData.IsReady || _queuePathData == null || !_queuePathData.IsReady
                || !_circleShape.IsReady)
            {
                Debug.LogError("TransformPath overview requires all providers to be ready.", this);
                return;
            }

            _queueManager.ConfigureRoute(_queuePathData);
            _queue = new TransformPathQueueLane(_queueManager, _queuePathData, _queueFollowers, () => _isInitialized && !_isPaused);
            _presentation = new TransformPathLanePresentation(transform, _routeLineMaterial);
            ResolveFreeCamera();
            _presentation.CreateRouteLines(_multiPathData);
            _presentation.CreateShapeLine(_circleShape);
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
            _shapeFollower?.Stop();
            _queue.StopAll();
            _presentation.DestroyRouteLines();
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
            // The shape lane is already playing from its follower's Play On Start.
            StartAllLanes();
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
        public void ShowShapeLane() => SetActiveLane(ETransformPathShowcaseLane.Shape);

        public void FocusActiveLane()
        {
            if (!_isInitialized || _freeCamera == null)
                return;
            _freeCamera.FocusOnBounds(_presentation.GetBounds(_activeLane));
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
        }

        public void TogglePauseAll()
        {
            if (_isPaused)
                ResumeAll();
            else
                PauseAll();
        }

        // Hidden lanes are paused already and remember whether they ran; only
        // the lane on show pauses here, so their memory survives Pause All.
        public void PauseAll()
        {
            if (!_isInitialized || _isPaused)
                return;
            if (_activeLane != ETransformPathShowcaseLane.Queue || _queueVisible)
                PauseLane(_activeLane);
            _isPaused = true;
        }

        public void ResumeAll()
        {
            if (!_isInitialized || !_isPaused)
                return;
            _isPaused = false;
            ResumeActiveLane();
        }

        public void ResetAll()
        {
            if (!_isInitialized)
                return;
            bool wasPaused = _isPaused;
            _isPaused = false;
            _pathFollower?.Stop();
            _multiPathFollower?.Stop();
            _shapeFollower?.Stop();
            // RESET starts the lane over, so BLOCK LEADER's hold goes with it.
            _queue.ReleaseHeldLeader();
            _queue.StopAll();

            StartShapePath();
            StartAllLanes();
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
            else if (_activeLane == ETransformPathShowcaseLane.Shape && _shapeFollower != null)
                _shapeFollower.Seek(Mathf.Repeat(_shapeFollower.Progress + delta, 1f));
        }

        public void BlockQueueLeader() => _queue?.HoldLeader();
        public void UnblockQueueLeader() => _queue?.ReleaseHeldLeader();

        public void ToggleQueueVisibility()
        {
            if (!_isInitialized)
                return;
            _queueVisible = !_queueVisible;
            bool queueShown = _activeLane == ETransformPathShowcaseLane.Queue && _queueVisible;
            if (_activeLane == ETransformPathShowcaseLane.Queue && !_queueVisible)
                _queue.PauseAndRemember();
            _presentation.SetVisible(ETransformPathShowcaseLane.Queue, queueShown);
            // Showing the queue resumes it there, but not while Pause All holds every lane.
            // Another lane on show is left alone, so its event pauses run their course.
            if (queueShown && !_isPaused)
                _queue.ResumeRemembered();
        }

        public string GetActiveStatusText()
        {
            if (!_isInitialized)
                return "TransformPath · initializing";
            switch (_activeLane)
            {
                case ETransformPathShowcaseLane.MultiPath:
                    return TransformPathLaneStatus.Multi(_multiPathData, _multiPathFollower);
                case ETransformPathShowcaseLane.Queue:
                    return TransformPathLaneStatus.Queue(_queue, _queueVisible, _isPaused);
                case ETransformPathShowcaseLane.Shape:
                    return TransformPathLaneStatus.Shape(_circleShape, _shapeFollower);
                default:
                    return TransformPathLaneStatus.Normal(_pathData, _pathFollower);
            }
        }

        #endregion


        #region Private Methods

        // Every lane but the shape lane starts here; only the selected one keeps moving.
        private void StartAllLanes()
        {
            StartNormalPath();
            StartMultiPath();
            _queue.Start();
            PauseUnselectedLanes();
            ApplyLanePresentation();
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

        // Play On Start began the first lap; a reset starts the shape again the same way.
        private void StartShapePath()
        {
            if (_shapeFollower != null && !_shapeFollower.IsMoving)
                _shapeFollower.Play(PathPlaybackRequest.Shape(_circleShape, loop: true));
        }

        private void SubscribeCompletionEvents()
        {
            if (_completionEventsSubscribed)
                return;
            _multiPathFollower.Completed += RestartMultiPath;
            _queue.SubscribeRestarts();
            _completionEventsSubscribed = true;
        }

        private void UnsubscribeCompletionEvents()
        {
            if (!_completionEventsSubscribed)
                return;
            if (_multiPathFollower != null)
                _multiPathFollower.Completed -= RestartMultiPath;
            _queue.UnsubscribeRestarts();
            _completionEventsSubscribed = false;
        }

        private void RestartMultiPath()
        {
            if (_isInitialized && !_isPaused)
                StartMultiPath();
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
            if (_activeLane != ETransformPathShowcaseLane.Shape)
                PauseLane(ETransformPathShowcaseLane.Shape);
        }

        private void PauseLane(ETransformPathShowcaseLane lane)
        {
            switch (lane)
            {
                case ETransformPathShowcaseLane.Normal:
                    _normalWasMovingWhenHidden = TransformPathQueueLane.IsRunning(_pathFollower);
                    _pathFollower?.Pause();
                    break;
                case ETransformPathShowcaseLane.MultiPath:
                    _multiWasMovingWhenHidden = TransformPathQueueLane.IsRunning(_multiPathFollower);
                    _multiPathFollower?.Pause();
                    break;
                case ETransformPathShowcaseLane.Queue:
                    _queue.PauseAndRemember();
                    break;
                case ETransformPathShowcaseLane.Shape:
                    _shapeWasMovingWhenHidden = TransformPathQueueLane.IsRunning(_shapeFollower);
                    _shapeFollower?.Pause();
                    break;
            }
        }

        private void ResumeActiveLane()
        {
            if (_activeLane == ETransformPathShowcaseLane.Normal && _normalWasMovingWhenHidden)
                _pathFollower.Resume();
            else if (_activeLane == ETransformPathShowcaseLane.MultiPath && _multiWasMovingWhenHidden)
                _multiPathFollower.Resume();
            else if (_activeLane == ETransformPathShowcaseLane.Queue && _queueVisible)
                _queue.ResumeRemembered();
            else if (_activeLane == ETransformPathShowcaseLane.Shape && _shapeWasMovingWhenHidden)
                _shapeFollower.Resume();
        }

        private void ApplyLanePresentation()
        {
            _presentation.SetVisible(ETransformPathShowcaseLane.Normal, _activeLane == ETransformPathShowcaseLane.Normal);
            _presentation.SetVisible(ETransformPathShowcaseLane.MultiPath, _activeLane == ETransformPathShowcaseLane.MultiPath);
            _presentation.SetVisible(ETransformPathShowcaseLane.Queue, _activeLane == ETransformPathShowcaseLane.Queue && _queueVisible);
            _presentation.SetVisible(ETransformPathShowcaseLane.Shape, _activeLane == ETransformPathShowcaseLane.Shape);
            if (!_isPaused)
                ResumeActiveLane();
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

        #endregion
    }
}
