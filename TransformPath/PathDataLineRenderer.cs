using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    /// <summary>
    /// <see cref="PathData"/>가 계산한 샘플 경로를 런타임 LineRenderer로 표시합니다.
    /// 갱신 시 항상 컨트롤 포인트의 현재 월드 위치를 기준으로 경로를 재계산합니다.
    /// </summary>
    public class PathDataLineRenderer : MonoBehaviour
    {
        #region Constants

        private const float DEFAULT_LINE_WIDTH = 0.05f;
        private const float POSITION_CHANGE_THRESHOLD = 0.001f;
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
        private bool _hasLoggedInvalidPath = false;
        private readonly List<Vector3> _controlPointsCache = new List<Vector3>();
        private Vector3[] _lastControlPointPositions = null;

        #endregion


        #region Unity Events

        private void Awake()
        {
            if (!TryGetComponent(out _lineRenderer))
                _lineRenderer = gameObject.AddComponent<LineRenderer>();
            ConfigureLineRenderer();
        }

        private void OnEnable()
        {
            _visible = _showOnEnable;
            Refresh();
        }

        private void OnDisable()
        {
            SetLineRendererEnabled(false);
        }

        private void LateUpdate()
        {
            if (HasControlPointPositionsChanged())
                Refresh();
        }

        #endregion


        #region Public Methods

        /// <summary>
        /// 컨트롤 포인트 현재 위치를 기준으로 PathData를 재계산하고 LineRenderer를 갱신합니다.
        /// </summary>
        public void Refresh()
        {
            if (!TryResolvePathData())
            {
                _hasValidPath = false;
                ApplyVisibility();
                return;
            }

            _pathData?.Init(forceReinit: true);

            Vector3[] pathPoints = _pathData.PathPoints;
            if (pathPoints == null || pathPoints.Length < MIN_PATH_POINT_COUNT)
            {
                LogInvalidPathOnce();
                _hasValidPath = false;
                ApplyVisibility();
                return;
            }

            _hasValidPath = true;
            _hasLoggedInvalidPath = false;

            _lineRenderer.positionCount = pathPoints.Length;
            _lineRenderer.SetPositions(pathPoints);
            CacheControlPointPositions();
            ApplyVisibility();
        }

        /// <summary>
        /// 경로 라인 표시 여부를 설정합니다.
        /// </summary>
        /// <param name="visible">표시 여부</param>
        public void SetVisible(bool visible)
        {
            _visible = visible;

            if (_visible)
                Refresh();
            else
                ApplyVisibility();
        }

        #endregion


        #region Private Methods

        private bool TryResolvePathData()
        {
            if (_pathData == null && !TryGetComponent(out _pathData))
            {
                LogInvalidPathOnce("PathDataLineRenderer: PathData 참조가 없습니다.");
                return false;
            }

            return _pathData != null;
        }

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

        private void LogInvalidPathOnce(string message = null)
        {
            if (_hasLoggedInvalidPath)
                return;

            _hasLoggedInvalidPath = true;
            Debug.LogWarning(message ?? "PathDataLineRenderer: 유효한 경로 포인트가 없습니다.");
        }

        private bool HasControlPointPositionsChanged()
        {
            if (!TryResolvePathData())
                return false;

            if (!_pathData.TryCopyWorldControlPoints(_controlPointsCache, clearDestination: true))
                return _lastControlPointPositions != null;

            if (_lastControlPointPositions == null || _lastControlPointPositions.Length != _controlPointsCache.Count)
                return true;

            float thresholdSqr = POSITION_CHANGE_THRESHOLD * POSITION_CHANGE_THRESHOLD;

            for (int i = 0; i < _controlPointsCache.Count; i++)
            {
                if ((_controlPointsCache[i] - _lastControlPointPositions[i]).sqrMagnitude > thresholdSqr)
                    return true;
            }

            return false;
        }

        private void CacheControlPointPositions()
        {
            if (_pathData == null || !_pathData.TryCopyWorldControlPoints(_controlPointsCache, clearDestination: true))
            {
                _lastControlPointPositions = null;
                return;
            }

            if (_controlPointsCache.Count > 0)
                _controlPointsCache.Capacity = Mathf.Max(_controlPointsCache.Capacity, _controlPointsCache.Count);

            if (_lastControlPointPositions == null || _lastControlPointPositions.Length != _controlPointsCache.Count)
                _lastControlPointPositions = new Vector3[_controlPointsCache.Count];

            for (int i = 0; i < _controlPointsCache.Count; i++)
                _lastControlPointPositions[i] = _controlPointsCache[i];
        }

        #endregion


#if UNITY_EDITOR
        private void Reset()
        {
            if (_pathData == null)
                TryGetComponent(out _pathData);

            if (!TryGetComponent(out _lineRenderer))
                _lineRenderer = gameObject.AddComponent<LineRenderer>();
            ConfigureLineRenderer();
        }

        private void OnValidate()
        {
            if (_lineRenderer == null && TryGetComponent(out _lineRenderer))
                ConfigureLineRenderer();
            else if (_lineRenderer != null)
            {
                _lineRenderer.startWidth = _lineWidth;
                _lineRenderer.endWidth = _lineWidth;

                if (_lineMaterial != null)
                    _lineRenderer.material = _lineMaterial;

                _lineRenderer.startColor = _lineColor;
                _lineRenderer.endColor = _lineColor;
            }
        }
#endif
    }
}
