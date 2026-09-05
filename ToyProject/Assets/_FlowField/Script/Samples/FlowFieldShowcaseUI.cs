using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Common.FlowField.Samples
{
    /// <summary>
    /// FlowField 쇼케이스의 상태와 두 가지 핵심 조작만 좌측 하단에 표시합니다.
    /// </summary>
    [DefaultExecutionOrder(110)]
    public sealed class FlowFieldShowcaseUI : MonoBehaviour
    {
        [SerializeField] private FlowFieldShowcaseOverviewController _overview;
        [SerializeField] private float _panelWidth = 390f;
        [SerializeField] private float _panelHeight = 185f;
        [SerializeField] private float _panelMargin = 16f;

        private Canvas _canvas;
        private Image _rootImage;
        private RectTransform _panelRect;
        private Text _statusText;
        private Text _gateButtonText;
        private Button _nextGoalButton;
        private Button _gateButton;
        private bool _listenersBound;
        private bool _isInitialized;

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        private void OnEnable()
        {
            if (!Application.isPlaying)
                return;

            if (_isInitialized)
                BindListeners();
            else
                Init();
        }

        public void Init()
        {
            if (_isInitialized)
                return;

            if (_overview == null)
                _overview = GetComponentInParent<FlowFieldShowcaseOverviewController>();
            if (_overview == null)
                throw new System.InvalidOperationException("FlowFieldShowcaseUI requires a FlowFieldShowcaseOverviewController in a parent object.");

            _canvas = GetComponent<Canvas>();
            if (_canvas == null)
                throw new System.InvalidOperationException("FlowFieldShowcaseUI requires a Canvas component on the same GameObject.");
            if (!IsFinite(_panelWidth) || _panelWidth <= 0f)
                throw new System.ArgumentOutOfRangeException(nameof(_panelWidth));
            if (!IsFinite(_panelHeight) || _panelHeight <= 0f)
                throw new System.ArgumentOutOfRangeException(nameof(_panelHeight));
            if (!IsFinite(_panelMargin) || _panelMargin < 0f)
                throw new System.ArgumentOutOfRangeException(nameof(_panelMargin));

            ConfigureCanvas();
            CreateCompactPanel();
            EnsureEventSystem();

            BindListeners();
            _isInitialized = true;
            RefreshGateButton();
        }

        private void Update()
        {
            if (!_isInitialized || _overview == null || !_overview.IsInitialized)
                return;

            BindListeners();
            RefreshGateButton();
        }

        private void BindListeners()
        {
            if (_nextGoalButton == null || _gateButton == null)
                return;

            _nextGoalButton.onClick.RemoveListener(HandleNextGoal);
            _nextGoalButton.onClick.AddListener(HandleNextGoal);
            _gateButton.onClick.RemoveListener(HandleToggleGate);
            _gateButton.onClick.AddListener(HandleToggleGate);
            _listenersBound = true;
        }

        private void ConfigureCanvas()
        {
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.worldCamera = null;
            _canvas.sortingOrder = 50;

            RectTransform rootRect = _canvas.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.anchoredPosition = Vector2.zero;
            rootRect.sizeDelta = Vector2.zero;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            rootRect.localPosition = Vector3.zero;
            rootRect.localScale = Vector3.one;
            rootRect.localRotation = Quaternion.identity;

            CanvasScaler scaler = GetComponent<CanvasScaler>();
            if (scaler == null)
                scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            if (GetComponent<GraphicRaycaster>() == null)
                gameObject.AddComponent<GraphicRaycaster>();

            _rootImage = GetComponent<Image>();
            if (_rootImage != null)
            {
                _rootImage.enabled = false;
                _rootImage.raycastTarget = false;
            }
        }

        private void CreateCompactPanel()
        {
            GameObject panelObject = new GameObject("Compact Panel", typeof(RectTransform), typeof(Image));
            panelObject.transform.SetParent(transform, false);
            _panelRect = panelObject.GetComponent<RectTransform>();
            _panelRect.anchorMin = Vector2.zero;
            _panelRect.anchorMax = Vector2.zero;
            _panelRect.pivot = Vector2.zero;
            _panelRect.anchoredPosition = new Vector2(_panelMargin, _panelMargin);
            _panelRect.sizeDelta = new Vector2(_panelWidth, _panelHeight);

            Image panelImage = panelObject.GetComponent<Image>();
            panelImage.color = new Color(0.015f, 0.025f, 0.05f, 0.84f);
            panelImage.raycastTarget = true;

            _statusText = GetComponentInChildren<Text>(true);
            if (_statusText == null)
            {
                GameObject statusObject = new GameObject("Status Text", typeof(RectTransform), typeof(Text));
                statusObject.transform.SetParent(_panelRect, false);
                _statusText = statusObject.GetComponent<Text>();
            }
            else
            {
                _statusText.transform.SetParent(_panelRect, false);
            }

            ConfigureStatusText(_statusText);
            _nextGoalButton = CreateButton(
                "Next Goal Button",
                "NEXT GOAL",
                new Vector2(0f, 0f),
                new Vector2(0.5f, 0.27f),
                new Vector2(12f, 12f),
                new Vector2(-4f, 0f));
            _gateButton = CreateButton(
                "Dynamic Gate Button",
                "DYNAMIC GATE: OFF",
                new Vector2(0.5f, 0f),
                new Vector2(1f, 0.27f),
                new Vector2(4f, 12f),
                new Vector2(-12f, 0f));
            _gateButtonText = _gateButton.GetComponentInChildren<Text>(true);
        }

        private void ConfigureStatusText(Text text)
        {
            RectTransform rect = text.rectTransform;
            rect.anchorMin = new Vector2(0f, 0.30f);
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(12f, 8f);
            rect.offsetMax = new Vector2(-12f, -8f);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;

            if (text.font == null)
                text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 12;
            text.fontStyle = FontStyle.Normal;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.color = Color.white;
            text.raycastTarget = false;
        }

        private Button CreateButton(
            string objectName,
            string label,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            GameObject buttonObject = new GameObject(objectName, typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(_panelRect, false);
            RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
            buttonRect.anchorMin = anchorMin;
            buttonRect.anchorMax = anchorMax;
            buttonRect.offsetMin = offsetMin;
            buttonRect.offsetMax = offsetMax;
            buttonRect.localScale = Vector3.one;
            buttonRect.localRotation = Quaternion.identity;

            Image buttonImage = buttonObject.GetComponent<Image>();
            buttonImage.color = new Color(0.10f, 0.26f, 0.42f, 0.96f);
            buttonImage.raycastTarget = true;

            Button button = buttonObject.GetComponent<Button>();
            button.targetGraphic = buttonImage;
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(0.10f, 0.26f, 0.42f, 0.96f);
            colors.highlightedColor = new Color(0.16f, 0.38f, 0.58f, 1f);
            colors.pressedColor = new Color(0.06f, 0.16f, 0.28f, 1f);
            colors.selectedColor = colors.highlightedColor;
            button.colors = colors;

            GameObject labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(buttonObject.transform, false);
            RectTransform labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(5f, 2f);
            labelRect.offsetMax = new Vector2(-5f, -2f);
            Text labelText = labelObject.GetComponent<Text>();
            labelText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            labelText.fontSize = 12;
            labelText.fontStyle = FontStyle.Bold;
            labelText.alignment = TextAnchor.MiddleCenter;
            labelText.horizontalOverflow = HorizontalWrapMode.Wrap;
            labelText.verticalOverflow = VerticalWrapMode.Overflow;
            labelText.color = Color.white;
            labelText.text = label;
            labelText.raycastTarget = false;

            return button;
        }

        private void EnsureEventSystem()
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null)
                eventSystem = FindFirstObjectByType<EventSystem>();

            if (eventSystem == null)
            {
                GameObject eventSystemObject = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                eventSystem = eventSystemObject.GetComponent<EventSystem>();
            }
            else if (eventSystem.GetComponent<StandaloneInputModule>() == null)
            {
                eventSystem.gameObject.AddComponent<StandaloneInputModule>();
            }
        }

        private void HandleNextGoal()
        {
            if (_overview != null && _overview.IsInitialized)
                _overview.AdvanceGoal();
        }

        private void HandleToggleGate()
        {
            if (_overview != null && _overview.IsInitialized)
                _overview.ToggleDynamicObstacle();
        }

        private void RefreshGateButton()
        {
            if (_gateButtonText == null || _overview == null || !_overview.IsInitialized)
                return;

            _gateButtonText.text = $"DYNAMIC GATE: {(_overview.DynamicObstacleEnabled ? "ON" : "OFF")}";
        }

        private void OnDestroy()
        {
            if (_listenersBound)
            {
                if (_nextGoalButton != null)
                    _nextGoalButton.onClick.RemoveListener(HandleNextGoal);
                if (_gateButton != null)
                    _gateButton.onClick.RemoveListener(HandleToggleGate);
            }
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
