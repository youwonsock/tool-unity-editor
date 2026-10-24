using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Common.OptimizeTool.Samples
{
    /// <summary>
    /// OptimizeTool 샘플의 결과 선택과 자유카메라 조작법을 표시하는 런타임 UGUI 패널입니다.
    /// </summary>
    [DefaultExecutionOrder(110)]
    public sealed class OptimizeToolShowcaseUI : MonoBehaviour
    {
        private const float PANEL_WIDTH = 420f;
        private const float PANEL_HEIGHT = 340f;
        private const float CONTENT_WIDTH = 388f;

        private static readonly Color PANEL_COLOR = new Color(0.025f, 0.04f, 0.07f, 0.9f);
        private static readonly Color ACTIVE_COLOR = new Color(0.15f, 0.65f, 1f, 1f);
        private static readonly Color BUTTON_COLOR = new Color(0.16f, 0.28f, 0.43f, 1f);
        private static readonly Color MUTED_TEXT_COLOR = new Color(0.58f, 0.67f, 0.78f, 1f);

        private OptimizeToolOverviewController _controller;
        private OptimizeToolOverviewBoard _board;
        private Transform _panelRoot;
        private Text _statusText;
        private Text _activeFeatureText;
        private Font _font;
        private Button _recordButton, _compareButton, _resetButton;
        private Text _keyboardHelp;
        private RectTransform _panelRect;
        private readonly List<Button> _featureButtons = new List<Button>();
        private readonly List<Image> _featureButtonImages = new List<Image>();
        private readonly Dictionary<Button, UnityAction> _buttonActions = new();

        private void Start()
        {
            Build();
        }

        private void OnEnable()
        {
            if (RestorePanelReferences())
            {
                BindControls();
                RefreshPresentation();
            }
        }

        private void OnDisable()
        {
            UnbindControls();
        }

        private void UnbindControls()
        {
            if (_controller != null) _controller.PresentationChanged -= RefreshPresentation;
            foreach (var binding in _buttonActions)
                if (binding.Key != null) binding.Key.onClick.RemoveListener(binding.Value);
            _buttonActions.Clear();
        }

        private bool RestorePanelReferences()
        {
            _controller = GetComponentInParent<OptimizeToolOverviewController>();
            _board = GetComponent<OptimizeToolOverviewBoard>();
            if (_controller == null || _board == null) return false;
            _panelRoot = _board.transform.Find("OptimizeTool Compact Panel");
            if (_panelRoot == null) return false;
            _panelRect = (RectTransform)_panelRoot;
            _statusText = _panelRoot.GetComponentInChildren<Text>(true);
            _activeFeatureText = _panelRoot.Find("Active Feature").GetComponent<Text>();
            _keyboardHelp = _panelRoot.Find("Keyboard Help").GetComponent<Text>();
            _recordButton = FindButton("RECORD");
            _compareButton = FindButton("COMPARE");
            _resetButton = FindButton("RESET");
            _featureButtons.Clear();
            _featureButtonImages.Clear();
            for (int i = 0; i < OptimizeToolSampleModes.Count; i++)
            {
                Button button = FindButton(OptimizeToolSampleModes.All[i].Button);
                _featureButtons.Add(button);
                _featureButtonImages.Add(button.GetComponent<Image>());
            }
            return true;
        }

        private Button FindButton(string label) => _panelRoot.Find(label + " Button").GetComponent<Button>();

        private void BindControls()
        {
            UnbindControls();
            for (int i = 0; i < _featureButtons.Count; i++)
            {
                int index = i;
                BindButton(_featureButtons[i], () => _controller.SelectFeature(index));
            }
            BindButton(FindButton("BAKE VIEW"), _controller.UseBakeView);
            BindButton(FindButton("FOCUS CAMERA"), _controller.FocusCamera);
            if (_controller.PhysicsDemo != null)
            {
                BindButton(_recordButton, _controller.PhysicsDemo.Record);
                BindButton(_compareButton, _controller.PhysicsDemo.Compare);
                BindButton(_resetButton, _controller.PhysicsDemo.ResetDemo);
            }
            _controller.PresentationChanged += RefreshPresentation;
        }

        private void BindButton(Button button, UnityAction action)
        {
            button.onClick.AddListener(action);
            _buttonActions.Add(button, action);
        }

        private void UnbindButton(Button button)
        {
            if (!_buttonActions.TryGetValue(button, out UnityAction action)) return;
            button.onClick.RemoveListener(action);
            _buttonActions.Remove(button);
        }

        private void Build()
        {
            if (RestorePanelReferences())
            {
                BindControls();
                RefreshPresentation();
                return;
            }

            _controller = GetComponentInParent<OptimizeToolOverviewController>();
            _board = GetComponent<OptimizeToolOverviewBoard>();
            if (_controller == null || _board == null)
                throw new InvalidOperationException("OptimizeToolShowcaseUI requires the overview controller and board.");

            _board.TryGetComponent(out Canvas canvas);
            _board.TryGetComponent(out CanvasScaler scaler);
            _statusText = _board.GetComponentInChildren<Text>(true);
            if (canvas == null || _statusText == null)
                throw new InvalidOperationException("OptimizeTool overview board requires Canvas and Text.");

            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.worldCamera = null;
            canvas.sortingOrder = 100;

            if (scaler == null)
                scaler = _board.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            _board.TryGetComponent(out Image rootBackground);
            if (rootBackground != null)
            {
                rootBackground.enabled = false;
                rootBackground.raycastTarget = false;
            }

            GameObject panelObject = new GameObject("OptimizeTool Compact Panel", typeof(RectTransform), typeof(Image));
            panelObject.transform.SetParent(_board.transform, false);
            panelObject.transform.SetAsFirstSibling();
            _panelRoot = panelObject.transform;
            panelObject.TryGetComponent(out RectTransform panelRect);
            _panelRect = panelRect;
            ConfigureBottomLeftRect(panelRect, new Vector2(24f, 24f), new Vector2(PANEL_WIDTH, PANEL_HEIGHT));
            panelObject.TryGetComponent(out Image panelImage);
            panelImage.color = PANEL_COLOR;
            panelImage.raycastTarget = true;

            _font = _statusText.font != null
                ? _statusText.font
                : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _statusText.transform.SetParent(_panelRoot, false);
            ConfigureText(_statusText, new Vector2(16f, -136f), new Vector2(CONTENT_WIDTH, 156f), 10, Color.white);

            CreateText(
                _panelRoot,
                "Title",
                "OPTIMIZE TOOL",
                new Vector2(16f, -12f),
                new Vector2(250f, 22f),
                18,
                Color.white,
                TextAnchor.MiddleLeft);
            _activeFeatureText = CreateText(
                _panelRoot,
                "Active Feature",
                string.Empty,
                new Vector2(16f, -36f),
                new Vector2(CONTENT_WIDTH, 18f),
                11,
                ACTIVE_COLOR,
                TextAnchor.MiddleLeft);

            for (int i = 0; i < OptimizeToolSampleModes.Count; i++)
                CreateFeatureButton(_panelRoot, OptimizeToolSampleModes.All[i].Button,
                    new Vector2(16f + i % 3 * 131f, -60f - i / 3 * 38f));
            CreateButton(_panelRoot, "BAKE VIEW", new Vector2(278f, -98f), new Vector2(126f, 32f), BUTTON_COLOR);
            CreateButton(
                _panelRoot,
                "FOCUS CAMERA",
                new Vector2(278f, -10f),
                new Vector2(126f, 24f),
                BUTTON_COLOR);

            _recordButton = CreateButton(_panelRoot, "RECORD", new Vector2(16f, -136f), new Vector2(126f, 32f),
                new Color(0.64f, 0.25f, 0.15f));
            _compareButton = CreateButton(_panelRoot, "COMPARE", new Vector2(147f, -136f), new Vector2(126f, 32f),
                BUTTON_COLOR);
            _resetButton = CreateButton(_panelRoot, "RESET", new Vector2(278f, -136f), new Vector2(126f, 32f),
                BUTTON_COLOR);

            _keyboardHelp = CreateText(
                _panelRoot,
                "Keyboard Help",
                "1-5 select · Space cycle · R original · F view",
                new Vector2(16f, -306f),
                new Vector2(CONTENT_WIDTH, 14f),
                9,
                MUTED_TEXT_COLOR,
                TextAnchor.MiddleLeft);
            CreateText(
                _panelRoot,
                "Camera Help",
                "RMB look/move · WASD · Q/E vertical · wheel speed · Shift boost",
                new Vector2(16f, -322f),
                new Vector2(CONTENT_WIDTH, 14f),
                9,
                MUTED_TEXT_COLOR,
                TextAnchor.MiddleLeft);

            EnsureEventSystem();
            BindControls();
            RefreshPresentation();
        }

        private void CreateFeatureButton(Transform parent, string label, Vector2 position)
        {
            Button button = CreateButton(
                parent,
                label,
                position,
                new Vector2(126f, 32f),
                BUTTON_COLOR);
            button.TryGetComponent(out Image image);
            _featureButtons.Add(button);
            _featureButtonImages.Add(image);
        }

        private void RefreshPresentation()
        {
            if (!_controller.IsInitialized)
                return;

            _activeFeatureText.text = "ACTIVE / " + _controller.GetFeatureName(_controller.ActiveIndex);
            bool physics = _controller.ActiveIndex == OptimizeToolSampleModes.Physics;
            OptimizeToolPhysicsDemo demo = _controller.PhysicsDemo;
            if (demo == null)
            {
                UnbindButton(_recordButton);
                UnbindButton(_compareButton);
                UnbindButton(_resetButton);
            }
            _recordButton.gameObject.SetActive(physics);
            _compareButton.gameObject.SetActive(physics);
            _resetButton.gameObject.SetActive(physics);
            _recordButton.interactable = demo != null && demo.CanRecord;
            _compareButton.interactable = demo != null && demo.CanCompare;
            _resetButton.interactable = demo != null && !demo.IsBusy;
            foreach (Button feature in _featureButtons) feature.interactable = demo == null || !demo.IsBusy;
            _panelRect.sizeDelta = new Vector2(PANEL_WIDTH, physics ? 460f : PANEL_HEIGHT);
            _statusText.rectTransform.anchoredPosition = new Vector2(16f, physics ? -180f : -136f);
            _statusText.rectTransform.sizeDelta = new Vector2(CONTENT_WIDTH, physics ? 216f : 156f);
            _statusText.fontSize = physics ? 12 : 10;
            _keyboardHelp.rectTransform.anchoredPosition = new Vector2(16f, physics ? -414f : -306f);
            _keyboardHelp.text = physics ? "C record · P compare · X reset · 1-5 modes · F view"
                : "1-5 select · Space cycle · R original · F view";
            _panelRoot.Find("Camera Help").GetComponent<RectTransform>().anchoredPosition
                = new Vector2(16f, physics ? -430f : -322f);
            for (int i = 0; i < _featureButtonImages.Count; i++)
            {
                _featureButtonImages[i].color = i == _controller.ActiveIndex
                    ? Color.Lerp(ACTIVE_COLOR, Color.white, 0.2f)
                    : BUTTON_COLOR;
            }
        }

        private void EnsureEventSystem()
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                GameObject eventSystemObject = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                eventSystem = eventSystemObject.GetComponent<EventSystem>();
            }
            if (eventSystem.GetComponent<StandaloneInputModule>() == null)
                eventSystem.gameObject.AddComponent<StandaloneInputModule>();
        }

        private Button CreateButton(Transform parent, string label, Vector2 position, Vector2 size, Color color)
        {
            GameObject buttonObject = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);
            buttonObject.TryGetComponent(out RectTransform rect);
            ConfigureRect(rect, position, size);

            buttonObject.TryGetComponent(out Image image);
            image.color = color;
            image.raycastTarget = true;

            buttonObject.TryGetComponent(out Button button);
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = color;
            colors.highlightedColor = Color.Lerp(color, Color.white, 0.22f);
            colors.pressedColor = Color.Lerp(color, Color.black, 0.18f);
            colors.selectedColor = colors.highlightedColor;
            button.colors = colors;
            CreateText(buttonObject.transform, "Label", label, Vector2.zero, size, 10, Color.white, TextAnchor.MiddleCenter);
            return button;
        }

        private Text CreateText(Transform parent, string name, string value, Vector2 position, Vector2 size, int fontSize, Color color, TextAnchor anchor)
        {
            GameObject textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);
            textObject.TryGetComponent(out Text text);
            text.font = _font != null ? _font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = value;
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            textObject.TryGetComponent(out RectTransform textRect);
            ConfigureRect(textRect, position, size);
            return text;
        }

        private void ConfigureText(Text text, Vector2 position, Vector2 size, int fontSize, Color color)
        {
            text.font = _font != null ? _font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.TryGetComponent(out RectTransform textRect);
            ConfigureRect(textRect, position, size);
        }

        private static void ConfigureBottomLeftRect(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
        }

        private static void ConfigureRect(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
        }
    }
}
