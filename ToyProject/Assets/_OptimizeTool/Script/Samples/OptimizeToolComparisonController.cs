using System;
using UnityEngine;

namespace Common.OptimizeTool.Samples
{
    /// <summary>
    /// 원본 메시 그룹과 MeshCombiner 결과를 번갈아 보여주는 안전한 런타임 비교 데모입니다.
    /// </summary>
    [DefaultExecutionOrder(0)]
    public sealed class OptimizeToolComparisonController : MonoBehaviour
    {
        [Header("Comparison Groups")]
        [SerializeField] private GameObject _originalGroup;
        [SerializeField] private GameObject _optimizedGroup;

        [Header("Display")]
        [SerializeField] private float _toggleInterval = 3f;
        [SerializeField] private bool _startWithOptimized;

        private float _toggleTimer;
        private bool _showOptimized;
        private int _originalRendererCount;
        private int _originalVertexCount;
        private int _optimizedRendererCount;
        private int _optimizedVertexCount;
        private bool _isInitialized;
        private bool _isFaulted;
        private Exception _fault;

        public bool ShowingOptimized => _showOptimized;

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("OptimizeToolComparisonController is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("OptimizeToolComparisonController is faulted; call Release before Init.", _fault);
            try
            {
                if (_originalGroup == null || _optimizedGroup == null)
                    throw new InvalidOperationException("Comparison groups must be assigned.");
                if (float.IsNaN(_toggleInterval) || float.IsInfinity(_toggleInterval) || _toggleInterval <= 0f)
                    throw new ArgumentOutOfRangeException(nameof(_toggleInterval));
                CacheMetrics();
                if (_originalRendererCount != 9 || _optimizedRendererCount != 1)
                    throw new ArgumentException("Comparison scene must contain 9 original renderers and 1 optimized renderer.");
                _isInitialized = true;
                SetVisible(_startWithOptimized);
            }
            catch (Exception exception)
            {
                _isInitialized = false;
                _isFaulted = true;
                if (_fault == null)
                    _fault = exception;
                throw;
            }
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("OptimizeToolComparisonController has not been initialized.");
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
        }

        private void Update()
        {
            if (_isFaulted)
                throw new InvalidOperationException("OptimizeToolComparisonController is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("OptimizeToolComparisonController is not initialized.");
            _toggleTimer += Time.deltaTime;
            if (_toggleTimer < _toggleInterval)
                return;

            _toggleTimer = 0f;
            SetVisible(!_showOptimized);
        }

        private void OnGUI()
        {
            if (!_isInitialized)
                return;
            GUI.Box(new Rect(16f, 16f, 360f, 112f), "OptimizeTool Sample");
            GUI.Label(
                new Rect(30f, 44f, 330f, 24f),
                $"현재: {(_showOptimized ? "결합 메시" : "원본 메시")}");
            GUI.Label(
                new Rect(30f, 68f, 330f, 24f),
                $"원본: {_originalRendererCount} Renderer / {_originalVertexCount} vertices");
            GUI.Label(
                new Rect(30f, 92f, 330f, 24f),
                $"결과: {_optimizedRendererCount} Renderer / {_optimizedVertexCount} vertices");
        }

        private void CacheMetrics()
        {
            GetMetrics(_originalGroup, out _originalRendererCount, out _originalVertexCount);
            GetMetrics(_optimizedGroup, out _optimizedRendererCount, out _optimizedVertexCount);
        }

        private void SetVisible(bool optimized)
        {
            _showOptimized = optimized;
            _originalGroup.SetActive(!optimized);
            _optimizedGroup.SetActive(optimized);
        }

        private static void GetMetrics(
            GameObject root,
            out int rendererCount,
            out int vertexCount)
        {
            rendererCount = 0;
            vertexCount = 0;

            if (root == null)
                throw new ArgumentNullException(nameof(root));

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            rendererCount = renderers.Length;

            MeshFilter[] meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
            foreach (MeshFilter meshFilter in meshFilters)
            {
                if (meshFilter.sharedMesh == null)
                    throw new ArgumentException($"MeshFilter '{meshFilter.name}' has no mesh.", nameof(root));
                vertexCount += meshFilter.sharedMesh.vertexCount;
            }

            SkinnedMeshRenderer[] skinnedMeshes =
                root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer skinnedMesh in skinnedMeshes)
            {
                if (skinnedMesh.sharedMesh == null)
                    throw new ArgumentException($"SkinnedMeshRenderer '{skinnedMesh.name}' has no mesh.", nameof(root));
                vertexCount += skinnedMesh.sharedMesh.vertexCount;
            }
        }
    }
}
