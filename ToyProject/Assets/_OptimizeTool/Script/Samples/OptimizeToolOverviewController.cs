using System;
using UnityEngine;

namespace Common.OptimizeTool.Samples
{
    /// <summary>
    /// Play Mode에서 사전 생성된 OptimizeTool 결과 그룹을 비교합니다.
    /// </summary>
    [DefaultExecutionOrder(10)]
    public sealed class OptimizeToolOverviewController : MonoBehaviour
    {
        [SerializeField] private GameObject[] _featureGroups;
        [SerializeField] private string[] _featureNames =
        {
            "Original Meshes",
            "Combined Mesh",
            "Backface Preview",
            "Occlusion Preview",
            "Physics Recorded",
        };
        [SerializeField] private OptimizeToolOverviewBoard _board;
        [SerializeField] private OptimizeToolFreeCamera _freeCamera;

        private readonly int[] _rendererCounts = new int[5];
        private readonly int[] _vertexCounts = new int[5];
        private int _activeIndex;
        private bool _isInitialized;
        private bool _isFaulted;
        private Exception _fault;

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;
        public int ActiveIndex => _activeIndex;
        public int ActiveRendererCount => _rendererCounts[_activeIndex];
        public int ActiveVertexCount => _vertexCounts[_activeIndex];
        public int FeatureCount => _featureGroups?.Length ?? 0;

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        private void Start()
        {
            ThrowIfUnavailable();
            ResolveFreeCamera();
            FocusCamera();
            RenderBoard();
        }

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("OptimizeToolOverviewController is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("OptimizeToolOverviewController is faulted; call Release before Init.", _fault);
            try
            {
                if (_featureGroups == null || _featureGroups.Length != 5)
                    throw new ArgumentException("OptimizeTool overview requires exactly five result groups.", nameof(_featureGroups));
                if (_featureNames == null || _featureNames.Length != 5)
                    throw new ArgumentException("OptimizeTool overview requires exactly five result names.", nameof(_featureNames));
                if (_board == null)
                    throw new InvalidOperationException("OptimizeTool overview requires a serialized board reference.");
                for (int i = 0; i < _featureGroups.Length; i++)
                {
                    if (_featureGroups[i] == null)
                        throw new InvalidOperationException($"OptimizeTool result group {i} is missing.");
                    GetMetrics(_featureGroups[i], out _rendererCounts[i], out _vertexCounts[i]);
                }
                if (_rendererCounts[0] != 9 || _rendererCounts[1] != 1)
                    throw new ArgumentException("OptimizeTool overview expects 9 original renderers and 1 combined renderer.");
                _isInitialized = true;
                SetVisible(0);
            }
            catch (Exception exception)
            {
                _fault = exception;
                _isFaulted = true;
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
            SetVisible(index);
            RenderBoard();
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
            if (Input.GetKeyDown(KeyCode.Alpha1)) SelectFeature(0);
            if (Input.GetKeyDown(KeyCode.Alpha2)) SelectFeature(1);
            if (Input.GetKeyDown(KeyCode.Alpha3)) SelectFeature(2);
            if (Input.GetKeyDown(KeyCode.Alpha4)) SelectFeature(3);
            if (Input.GetKeyDown(KeyCode.Alpha5)) SelectFeature(4);
            if (Input.GetKeyDown(KeyCode.Space)) SelectFeature((_activeIndex + 1) % _featureGroups.Length);
            if (Input.GetKeyDown(KeyCode.R)) SelectFeature(0);
            RenderBoard();
        }

        private void SetVisible(int index)
        {
            if (index < 0 || index >= _featureGroups.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            _activeIndex = index;
            for (int i = 0; i < _featureGroups.Length; i++)
                _featureGroups[i].SetActive(i == index);
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

        private void RenderBoard()
        {
            if (!_board.IsInitialized)
                throw new InvalidOperationException("OptimizeToolOverviewBoard must be initialized before rendering.");
            string metrics = string.Empty;
            for (int i = 0; i < _featureGroups.Length; i++)
                metrics += $"{_featureNames[i]}: {_rendererCounts[i]} R / {_vertexCounts[i]} V\n";
            _board.Render(
                "OPTIMIZE TOOL SHOWCASE\n"
                + $"Active: {_featureNames[_activeIndex]}\n"
                + metrics
                + "Buttons / 1-5 Select | Space Cycle | R Original\n"
                + "Results are pre-generated in Settings/Generated; Play does not write assets.");
        }

        private static void GetMetrics(GameObject root, out int rendererCount, out int vertexCount)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            rendererCount = renderers.Length;
            vertexCount = 0;
            for (int i = 0; i < filters.Length; i++)
            {
                if (filters[i].sharedMesh == null)
                    throw new ArgumentException($"MeshFilter '{filters[i].name}' has no mesh.", nameof(root));
                vertexCount += filters[i].sharedMesh.vertexCount;
            }
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("OptimizeToolOverviewController has not been initialized.");
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
                throw new InvalidOperationException("OptimizeToolOverviewController is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("OptimizeToolOverviewController is not initialized.");
        }
    }
}
