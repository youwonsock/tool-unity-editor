using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// <see cref="PathData"/>가 계산한 샘플 경로를 런타임 LineRenderer로 표시합니다.
    /// 갱신 시 항상 컨트롤 포인트의 현재 월드 위치를 기준으로 경로를 재계산합니다.
    /// </summary>
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(LineRenderer))]
    public class PathDataLineRenderer : MonoBehaviour
    {
        #region Constants

        private const float DEFAULT_LINE_WIDTH = 0.05f;
        private const int MIN_PATH_POINT_COUNT = 2;

        #endregion


        #region Member Variables

        [Header("References")]
        [SerializeField] private PathData _pathData = null;

        [Header("Line Settings")]
        [SerializeField] private float _lineWidth = DEFAULT_LINE_WIDTH;
        [Tooltip("URP 라인용 Unlit/Particle 머티리얼을 할당하세요. 비우면 색만 적용됩니다.")]
        [SerializeField] private Material _lineMaterial = null;
        [SerializeField] private Color _lineColor = Color.cyan;

        [Header("Visibility")]
        [SerializeField] private bool _showOnEnable = true;

        private LineRenderer _lineRenderer = null;
        private bool _visible = true;
        private bool _hasValidPath = false;
        private bool _isInitialized;
        private bool _isFaulted;
        private Exception _fault;

        #endregion


        #region Unity Events

        private void Awake()
        {
            _lineRenderer = GetComponent<LineRenderer>();
            if (!Application.isPlaying)
                return;
            ConfigureLineRenderer();
            Init();
            Refresh();
        }

        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("PathDataLineRenderer is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("PathDataLineRenderer is faulted; call Release before Init.", _fault);
            try
            {
                if (_lineRenderer == null)
                    _lineRenderer = GetComponent<LineRenderer>();
                if (_lineRenderer == null || _pathData == null)
                    throw new InvalidOperationException("PathDataLineRenderer requires LineRenderer and PathData references.");
                ConfigureLineRenderer();
                _isInitialized = true;
                if (isActiveAndEnabled)
                {
                    _pathData.PathChanged -= HandlePathChanged;
                    _pathData.PathChanged += HandlePathChanged;
                }
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

        private void OnEnable()
        {
            if (!_isInitialized)
                return;
            _pathData.PathChanged -= HandlePathChanged;
            _pathData.PathChanged += HandlePathChanged;
            _visible = _showOnEnable;
            Refresh();
        }

        private void OnDisable()
        {
            if (_pathData != null)
                _pathData.PathChanged -= HandlePathChanged;
            SetLineRendererEnabled(false);
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("PathDataLineRenderer has not been initialized.");
            if (_pathData != null)
                _pathData.PathChanged -= HandlePathChanged;
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
        }

        #endregion


        #region Public Methods

        /// <summary>
        /// 컨트롤 포인트 현재 위치를 기준으로 PathData를 재계산하고 LineRenderer를 갱신합니다.
        /// </summary>
        public void Refresh()
        {
            if (_isFaulted)
                throw new InvalidOperationException("PathDataLineRenderer is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("PathDataLineRenderer is not initialized.");
            if (_pathData == null || !_pathData.IsReady)
                throw new InvalidOperationException("PathData must be initialized and ready before rendering.");

            Vector3[] pathPoints = _pathData.PathPoints;
            if (pathPoints == null || pathPoints.Length < MIN_PATH_POINT_COUNT)
                throw new ArgumentException("PathData does not contain enough sampled points.");

            _hasValidPath = true;
            _lineRenderer.positionCount = pathPoints.Length;
            _lineRenderer.SetPositions(pathPoints);
            ApplyVisibility();
        }

        /// <summary>
        /// 경로 라인 표시 여부를 설정합니다.
        /// </summary>
        /// <param name="visible">표시 여부</param>
        public void SetVisible(bool visible)
        {
            if (_isFaulted)
                throw new InvalidOperationException("PathDataLineRenderer is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("PathDataLineRenderer is not initialized.");
            _visible = visible;

            if (_visible)
                Refresh();
            else
                ApplyVisibility();
        }

        #endregion


        #region Private Methods

        private void HandlePathChanged() => Refresh();

        private void ConfigureLineRenderer()
        {
            if (_lineRenderer == null)
                return;

            _lineRenderer.startWidth = _lineWidth;
            _lineRenderer.endWidth = _lineWidth;
            _lineRenderer.useWorldSpace = true;
            _lineRenderer.enabled = false;

            if (_lineMaterial != null)
                _lineRenderer.material = _lineMaterial;

            _lineRenderer.startColor = _lineColor;
            _lineRenderer.endColor = _lineColor;
        }

        private void ApplyVisibility()
        {
            SetLineRendererEnabled(_visible && _hasValidPath);
        }

        private void SetLineRendererEnabled(bool enabled)
        {
            if (_lineRenderer == null)
                return;

            if (_lineRenderer.enabled == enabled)
                return;

            _lineRenderer.enabled = enabled;
        }

        #endregion


#if UNITY_EDITOR
        private void Reset()
        {
            _lineRenderer = GetComponent<LineRenderer>();
            ConfigureLineRenderer();
        }

        private void OnValidate()
        {
            // Inspector edits are applied by Init/Release boundaries. OnValidate
            // only observes serialized values and never mutates scene components.
        }
#endif
    }
}
