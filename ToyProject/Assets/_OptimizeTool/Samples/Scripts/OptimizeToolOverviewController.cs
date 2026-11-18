using System;
using UnityEngine;
using System.Text;

namespace Common.OptimizeTool.Samples
{
    /// <summary>
    /// Play Mode에서 사전 생성된 OptimizeTool 결과 그룹을 비교합니다.
    /// </summary>
    [DefaultExecutionOrder(10)]
    public sealed class OptimizeToolOverviewController : MonoBehaviour
    {
        [SerializeField] private GameObject[] _featureGroups;
        [SerializeField] private string[] _featureNames = DefaultNames();
        [SerializeField] private OptimizeToolOverviewBoard _board;
        [SerializeField] private OptimizeToolFreeCamera _freeCamera;
        [SerializeField] private Transform _bakeView;
        [SerializeField] private OptimizeToolPhysicsDemo _physicsDemo;

        private MeshSampleMetrics[] _metrics;
        private int _activeIndex;
        private bool _isInitialized;
        private Exception _fault;
        private string[] _meshBoardText;
        private double _nextPhysicsPresentation;
        private OptimizeToolPhysicsDemo.DemoState _lastPhysicsState;
        private bool _lastCanCompare;
        private bool _lastHadPhysicsDemo;
        public event Action PresentationChanged;

        private static string[] DefaultNames()
        {
            var names = new string[OptimizeToolSampleModes.Count];
            for (int i = 0; i < names.Length; i++) names[i] = OptimizeToolSampleModes.All[i].Name;
            return names;
        }

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _fault != null;
        public int ActiveIndex => _activeIndex;
        public int ActiveRendererCount => _metrics[_activeIndex].Renderers;
        public int ActiveVertexCount => _metrics[_activeIndex].Vertices;
        public long ActiveTriangleCount => _metrics[_activeIndex].Triangles;
        public int FeatureCount => _featureGroups?.Length ?? 0;
        public OptimizeToolPhysicsDemo PhysicsDemo => _physicsDemo;

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        private void Start()
        {
            ThrowIfUnavailable();
            ResolveFreeCamera();
            UseBakeView();
            RenderBoard();
        }

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("OptimizeToolOverviewController is already initialized.");
            if (IsFaulted)
                throw new InvalidOperationException("OptimizeToolOverviewController is faulted; call Release before Init.", _fault);
            try
            {
                if (_featureGroups == null || _featureGroups.Length != OptimizeToolSampleModes.Count)
                    throw new ArgumentException("Rebuild the Physics Recorder demo to assign five result groups.", nameof(_featureGroups));
                if (_featureNames == null || _featureNames.Length != _featureGroups.Length || _physicsDemo == null)
                    throw new ArgumentException("OptimizeTool overview requires five names and a Physics Recorder demo.");
                if (_bakeView == null)
                    throw new InvalidOperationException("Rebuild the showcase assets to assign the bake view.");
                if (_board == null)
                    throw new InvalidOperationException("OptimizeTool overview requires a serialized board reference.");
                _metrics = new MeshSampleMetrics[FeatureCount];
                for (int i = 0; i < _featureGroups.Length; i++)
                {
                    if (_featureGroups[i] == null)
                        throw new InvalidOperationException($"OptimizeTool result group {i} is missing.");
                    _metrics[i] = MeshSampleMetrics.Capture(_featureGroups[i]);
                }
                if (_metrics[0].Renderers != 9 || _metrics[1].Renderers != 1)
                    throw new ArgumentException("OptimizeTool overview expects 9 original renderers and 1 combined renderer.");
                CacheMeshBoardText();
                _isInitialized = true;
                SetVisible(OptimizeToolSampleModes.Original);
            }
            catch (Exception exception)
            {
                _fault = exception;
                throw;
            }
        }

        public string GetFeatureName(int index)
        {
            ThrowIfUnavailable();
            if (index < 0 || index >= _featureNames.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _featureNames[index];
        }

        public void SelectFeature(int index)
        {
            ThrowIfUnavailable();
            if (index < 0 || index >= FeatureCount) throw new ArgumentOutOfRangeException(nameof(index));
            if ((_physicsDemo != null && _physicsDemo.IsBusy) || index == _activeIndex) return;
            bool changingPhysics = index != _activeIndex && (index == OptimizeToolSampleModes.Physics || _activeIndex == OptimizeToolSampleModes.Physics);
            SetVisible(index);
            if (changingPhysics) UseBakeView();
            RenderBoard();
        }

        public void UseBakeView()
        {
            ThrowIfUnavailable();
            ResolveFreeCamera();
            Transform view = _activeIndex == OptimizeToolSampleModes.Physics && _physicsDemo != null
                ? _physicsDemo.View : _bakeView;
            if (_freeCamera == null) return;
            if (_activeIndex == OptimizeToolSampleModes.Physics && TryGetActiveBounds(out Bounds bounds) && Camera.main != null)
            {
                // Keep both arenas visible in portrait Game Views as well as landscape.
                Camera camera = Camera.main;
                float halfFov = camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
                float halfAngle = Mathf.Atan(Mathf.Tan(halfFov) * Mathf.Min(1f, camera.aspect));
                float distance = Mathf.Max(Vector3.Distance(view.position, bounds.center),
                    bounds.extents.magnitude / Mathf.Sin(halfAngle) + 0.5f);
                _freeCamera.SetPose(bounds.center - view.forward * distance, view.rotation);
            }
            else _freeCamera.SetPose(view.position, view.rotation);
        }

        public void FocusCamera()
        {
            ThrowIfUnavailable();
            ResolveFreeCamera();
            if (_freeCamera == null || !TryGetActiveBounds(out Bounds bounds))
                return;
            _freeCamera.FocusOnBounds(bounds);
        }

        private void Update()
        {
            ThrowIfUnavailable();
            for (int i = 0; i < FeatureCount && i < 9; i++)
                if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i))) SelectFeature(i);
            if (_activeIndex == OptimizeToolSampleModes.Physics && _physicsDemo != null)
            {
                if (Input.GetKeyDown(KeyCode.C)) _physicsDemo.Record();
                if (Input.GetKeyDown(KeyCode.P)) _physicsDemo.Compare();
                if (Input.GetKeyDown(KeyCode.X)) _physicsDemo.ResetDemo();
            }
            if (Input.GetKeyDown(KeyCode.Space)) SelectFeature((_activeIndex + 1) % _featureGroups.Length);
            if (Input.GetKeyDown(KeyCode.R)) { SelectFeature(0); UseBakeView(); }
            if (Input.GetKeyDown(KeyCode.F)) UseBakeView();
            bool hasPhysicsDemo = _physicsDemo != null;
            if (!hasPhysicsDemo) _physicsDemo = null; // Release a destroyed Unity object's managed wrapper too.
            if (hasPhysicsDemo != _lastHadPhysicsDemo
                || (_activeIndex == OptimizeToolSampleModes.Physics
                    && ((hasPhysicsDemo && (_lastPhysicsState != _physicsDemo.State || _lastCanCompare != _physicsDemo.CanCompare))
                        || Time.unscaledTimeAsDouble >= _nextPhysicsPresentation)))
                RenderBoard();
        }

        private void SetVisible(int index)
        {
            if (index < 0 || index >= _featureGroups.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            _activeIndex = index;
            for (int i = 0; i < _featureGroups.Length; i++)
                _featureGroups[i].SetActive(i == index);
            if (_physicsDemo != null) _physicsDemo.SetPresentationActive(index == OptimizeToolSampleModes.Physics);
        }

        private void ResolveFreeCamera()
        {
            if (_freeCamera != null)
                return;
            if (Camera.main != null)
                Camera.main.TryGetComponent(out _freeCamera);
            if (_freeCamera == null)
                _freeCamera = FindFirstObjectByType<OptimizeToolFreeCamera>();
        }

        private bool TryGetActiveBounds(out Bounds bounds)
        {
            Renderer[] renderers = _featureGroups[_activeIndex].GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                bounds = default;
                return false;
            }

            bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            return true;
        }

        private void CacheMeshBoardText()
        {
            var metrics = new StringBuilder("MODE                         R / V / TRI\n");
            for (int i = 0; i < OptimizeToolSampleModes.MeshCount; i++)
                metrics.AppendLine($"{_featureNames[i]}: {_metrics[i].Renderers} / {_metrics[i].Vertices} / {_metrics[i].Triangles}");
            _meshBoardText = new string[OptimizeToolSampleModes.MeshCount];
            for (int i = 0; i < _meshBoardText.Length; i++)
            {
                float reduction = _metrics[0].Triangles == 0 ? 0 : 100f * (1f - (float)_metrics[i].Triangles / _metrics[0].Triangles);
                _meshBoardText[i] = metrics + $"\nActive triangle reduction: {reduction:0.0}%\n"
                    + "Counts describe geometry, not measured draw calls.\n"
                    + "Culling is baked for BAKE VIEW; moving reveals removed geometry.\n"
                    + "PHYSICS records and compares live motion in the Editor.";
            }
        }

        private void RenderBoard()
        {
            if (!_board.IsInitialized)
                throw new InvalidOperationException("OptimizeToolOverviewBoard must be initialized before rendering.");
            if (_activeIndex == OptimizeToolSampleModes.Physics)
            {
                _board.Render(_physicsDemo != null ? _physicsDemo.GetStatusText() : "Physics Recorder demo is unavailable.");
                if (_physicsDemo != null) _lastPhysicsState = _physicsDemo.State;
                _lastCanCompare = _physicsDemo != null && _physicsDemo.CanCompare;
                _nextPhysicsPresentation = Time.unscaledTimeAsDouble + 0.1d;
            }
            else _board.Render(_meshBoardText[_activeIndex]);
            _lastHadPhysicsDemo = _physicsDemo != null;
            PresentationChanged?.Invoke();
        }

        public void Release()
        {
            if (!_isInitialized && !IsFaulted)
                throw new InvalidOperationException("OptimizeToolOverviewController has not been initialized.");
            _isInitialized = false;
            _fault = null;
        }

        private void OnDestroy()
        {
            if (_isInitialized || IsFaulted)
                Release();
        }

        private void ThrowIfUnavailable()
        {
            if (IsFaulted)
                throw new InvalidOperationException("OptimizeToolOverviewController is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("OptimizeToolOverviewController is not initialized.");
        }
    }
}
