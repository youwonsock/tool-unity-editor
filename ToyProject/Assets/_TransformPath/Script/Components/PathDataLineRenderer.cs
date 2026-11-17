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

        #endregion


        #region Properties

        public bool IsInitialized => _isInitialized;

        #endregion


        #region Unity Events

        public void Init()
        {
            if (_isInitialized)
                return;
            if (_lineRenderer == null)
                TryGetComponent(out _lineRenderer);
            if (_lineRenderer == null || _pathData == null)
            {
                Debug.LogError("PathDataLineRenderer requires LineRenderer and PathData references.", this);
                return;
            }
            ConfigureLineRenderer();
            _isInitialized = true;
            if (isActiveAndEnabled)
            {
                _pathData.PathChanged -= HandlePathChanged;
                _pathData.PathChanged += HandlePathChanged;
            }
        }

        public void Release()
        {
            if (_pathData != null)
                _pathData.PathChanged -= HandlePathChanged;
            _isInitialized = false;
            _hasValidPath = false;
            SetLineRendererEnabled(false);
        }

        private void Awake()
        {
            TryGetComponent(out _lineRenderer);
            if (!Application.isPlaying)
                return;
            ConfigureLineRenderer();
            Init();
            Refresh();
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
            Release();
        }

        #endregion


        #region Public Methods

        /// <summary>
        /// 컨트롤 포인트 현재 위치를 기준으로 PathData를 재계산하고 LineRenderer를 갱신합니다.
        /// </summary>
        public void Refresh()
        {
            if (!_isInitialized)
                Init();
            if (_pathData == null || !_pathData.IsReady)
            {
                _hasValidPath = false;
                ApplyVisibility();
                return;
            }

            int samplePointCount = _pathData.SamplePointCount;
            if (samplePointCount < MIN_PATH_POINT_COUNT)
            {
                _hasValidPath = false;
                ApplyVisibility();
                return;
            }

            _hasValidPath = true;
            _lineRenderer.positionCount = samplePointCount;
            for (int i = 0; i < samplePointCount; i++)
                _lineRenderer.SetPosition(i, _pathData.GetSamplePoint(i));
            ApplyVisibility();
        }

        /// <summary>
        /// 경로 라인 표시 여부를 설정합니다.
        /// </summary>
        /// <param name="visible">표시 여부</param>
        public void SetVisible(bool visible)
        {
            if (!_isInitialized)
                Init();
            if (!_isInitialized)
                return;
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
            TryGetComponent(out _lineRenderer);
            ConfigureLineRenderer();
        }

#endif
    }
}
