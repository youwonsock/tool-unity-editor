using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Common.TransformPath.Samples
{
    /// <summary>
    /// 선택된 TransformPath lane의 설정과 상태만 표시하는 컴팩트 uGUI 패널입니다.
    /// </summary>
    [DefaultExecutionOrder(110)]
    public sealed class TransformPathShowcaseUI : MonoBehaviour
    {
        private const float BOARD_WIDTH = 420f;
        private const float BOARD_HEIGHT = 346f;
        private const float CONTENT_WIDTH = 388f;

        private static readonly Color PANEL_COLOR = new Color(0.025f, 0.04f, 0.07f, 0.9f);
        private static readonly Color NORMAL_COLOR = new Color(0.15f, 0.65f, 1f, 1f);
        private static readonly Color MULTI_COLOR_A = new Color(0.8f, 0.35f, 1f, 1f);
        private static readonly Color QUEUE_COLOR = new Color(1f, 0.65f, 0.1f, 1f);
        private static readonly Color COMMON_BUTTON_COLOR = new Color(0.16f, 0.28f, 0.43f, 1f);
        private static readonly Color DARK_BUTTON_COLOR = new Color(0.11f, 0.18f, 0.28f, 1f);

        private TransformPathOverviewController _controller;
        private TransformPathOverviewBoard _board;
        private Transform _panelRoot;
        private Text _statusText;
        private Text _activeLaneText;
        private Text _pauseButtonLabel;
        private Text _queueVisibilityLabel;
        private Font _font;
        private GameObject _normalSettings;
        private GameObject _multiSettings;
        private GameObject _queueSettings;
        private bool _isBuilt;
        private int _lastLane = -1;
        private readonly List<Image> _laneButtonImages = new List<Image>();

        private void Start()
        {
            Build();
        }

        private void Update()
        {
            if (!_isBuilt || _controller == null)
                return;

            RefreshPresentation();
        }

        private void Build()
        {
            TryGetComponent(out _controller);
            _board = GetComponentInChildren<TransformPathOverviewBoard>(true);
            if (_controller == null || _board == null)
                throw new InvalidOperationException("TransformPathShowcaseUI requires the overview controller and board.");

            _board.TryGetComponent(out Canvas canvas);
            _board.TryGetComponent(out RectTransform boardRect);
            _statusText = _board.GetComponentInChildren<Text>(true);
            if (canvas == null || boardRect == null || _statusText == null)
                throw new InvalidOperationException("TransformPath overview board requires Canvas, RectTransform, and Text.");

            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.worldCamera = null;
            canvas.sortingOrder = 100;

            _board.TryGetComponent(out CanvasScaler scaler);
            if (scaler != null)
            {
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;
            }

            // A root Canvas always covers the display. Keep its old Image from
            // painting a full-screen backdrop and create a real compact panel child.
            _board.TryGetComponent(out Image rootBackground);
            if (rootBackground != null)
            {
                rootBackground.enabled = false;
                rootBackground.raycastTarget = false;
            }

            GameObject panelObject = new GameObject("TransformPath Compact Panel", typeof(RectTransform), typeof(Image));
            panelObject.transform.SetParent(_board.transform, false);
            panelObject.transform.SetAsFirstSibling();
            _panelRoot = panelObject.transform;
            panelObject.TryGetComponent(out RectTransform panelRect);
            ConfigureBottomLeftRect(panelRect, new Vector2(24f, 24f), new Vector2(BOARD_WIDTH, BOARD_HEIGHT));
            panelObject.TryGetComponent(out Image panelImage);
            panelImage.color = PANEL_COLOR;
            panelImage.raycastTarget = true;

            // Move the serialized board text into the compact child panel so the
            // board component can still own its status Text reference.
            _statusText.transform.SetParent(_panelRoot, false);
            _font = _statusText.font;
            ConfigureText(_statusText, new Vector2(16f, -246f), new Vector2(CONTENT_WIDTH, 64f), 10, Color.white);

            CreateText(
                _panelRoot,
                "Title",
                "TRANSFORM PATH SHOWCASE",
                new Vector2(16f, -12f),
                new Vector2(CONTENT_WIDTH, 22f),
                18,
                Color.white,
                TextAnchor.MiddleLeft);
            _activeLaneText = CreateText(
                _panelRoot,
                "Active Lane",
                string.Empty,
                new Vector2(16f, -36f),
                new Vector2(CONTENT_WIDTH, 18f),
                11,
                NORMAL_COLOR,
                TextAnchor.MiddleLeft);

            CreateLaneButtons(_panelRoot);
            CreateCommonControls(_panelRoot);
            CreateSettingsGroups(_panelRoot);

            CreateText(
                _panelRoot,
                "Keyboard Help",
                "1/2/3 curve preset · Space pause · R reset · ←/→ seek · Q queue",
                new Vector2(16f, -316f),
                new Vector2(CONTENT_WIDTH, 14f),
                9,
                new Color(0.58f, 0.67f, 0.78f, 1f),
                TextAnchor.MiddleLeft);
            CreateText(
                _panelRoot,
                "Camera Help",
                "RMB look/move · WASD · Q/E vertical · wheel speed · Shift boost",
                new Vector2(16f, -331f),
                new Vector2(CONTENT_WIDTH, 14f),
                9,
                new Color(0.58f, 0.67f, 0.78f, 1f),
                TextAnchor.MiddleLeft);

            EnsureEventSystem();

            _isBuilt = true;
            RefreshPresentation();
        }

        private void CreateLaneButtons(Transform parent)
        {
            const float GAP = 5f;
            float width = (CONTENT_WIDTH - GAP * 2f) / 3f;
            CreateLaneButton(parent, "NORMAL", new Vector2(16f, -60f), new Vector2(width, 32f), NORMAL_COLOR, _controller.ShowNormalLane);
            CreateLaneButton(parent, "MULTI", new Vector2(16f + width + GAP, -60f), new Vector2(width, 32f), MULTI_COLOR_A, _controller.ShowMultiPathLane);
            CreateLaneButton(parent, "QUEUE", new Vector2(16f + (width + GAP) * 2f, -60f), new Vector2(width, 32f), QUEUE_COLOR, _controller.ShowQueuedLane);
        }

        private void CreateLaneButton(Transform parent, string label, Vector2 position, Vector2 size, Color color, Action action)
        {
            Button button = CreateButton(parent, label, position, size, color, action);
            button.TryGetComponent(out Image image);
            _laneButtonImages.Add(image);
        }

        private void CreateCommonControls(Transform parent)
        {
            Button pauseButton = CreateButton(
                parent,
                "PAUSE",
                new Vector2(16f, -100f),
                new Vector2(122f, 32f),
                COMMON_BUTTON_COLOR,
                _controller.TogglePauseAll);
            _pauseButtonLabel = pauseButton.GetComponentInChildren<Text>(true);
            CreateButton(parent, "RESET", new Vector2(144f, -100f), new Vector2(90f, 32f), COMMON_BUTTON_COLOR, _controller.ResetAll);
            CreateButton(parent, "FOCUS CAMERA", new Vector2(240f, -100f), new Vector2(164f, 32f), COMMON_BUTTON_COLOR, _controller.FocusActiveLane);
        }

        private void CreateSettingsGroups(Transform parent)
        {
            _normalSettings = CreateSettingsGroup(parent, "Normal Settings");
            CreateText(_normalSettings.transform, "Normal Header", "NORMAL PATH SETTINGS", new Vector2(16f, -142f), new Vector2(CONTENT_WIDTH, 18f), 11, NORMAL_COLOR, TextAnchor.MiddleLeft);
            CreateButton(_normalSettings.transform, "LINEAR", new Vector2(16f, -166f), new Vector2(122f, 32f), DARK_BUTTON_COLOR, () => _controller.SelectPresentationMode(0));
            CreateButton(_normalSettings.transform, "INTERPOLATE", new Vector2(143f, -166f), new Vector2(130f, 32f), DARK_BUTTON_COLOR, () => _controller.SelectPresentationMode(1));
            CreateButton(_normalSettings.transform, "APPROXIMATE", new Vector2(278f, -166f), new Vector2(126f, 32f), DARK_BUTTON_COLOR, () => _controller.SelectPresentationMode(2));
            CreateButton(_normalSettings.transform, "SEEK -", new Vector2(16f, -204f), new Vector2(88f, 32f), DARK_BUTTON_COLOR, _controller.SeekBackward);
            CreateButton(_normalSettings.transform, "SEEK +", new Vector2(110f, -204f), new Vector2(88f, 32f), DARK_BUTTON_COLOR, _controller.SeekForward);
            _multiSettings = CreateSettingsGroup(parent, "Multi Settings");
            CreateText(_multiSettings.transform, "Multi Header", "MULTI PATH SETTINGS", new Vector2(16f, -142f), new Vector2(CONTENT_WIDTH, 18f), 11, MULTI_COLOR_A, TextAnchor.MiddleLeft);
            CreateButton(_multiSettings.transform, "SEEK -", new Vector2(16f, -166f), new Vector2(92f, 32f), DARK_BUTTON_COLOR, _controller.SeekBackward);
            CreateButton(_multiSettings.transform, "SEEK +", new Vector2(114f, -166f), new Vector2(92f, 32f), DARK_BUTTON_COLOR, _controller.SeekForward);
            CreateText(_multiSettings.transform, "Multi Hint", "Two independent segments · global/local progress", new Vector2(216f, -166f), new Vector2(188f, 32f), 9, new Color(0.78f, 0.7f, 0.9f, 1f), TextAnchor.MiddleLeft);

            _queueSettings = CreateSettingsGroup(parent, "Queue Settings");
            CreateText(_queueSettings.transform, "Queue Header", "QUEUED PATH SETTINGS", new Vector2(16f, -142f), new Vector2(CONTENT_WIDTH, 18f), 11, QUEUE_COLOR, TextAnchor.MiddleLeft);
            CreateButton(_queueSettings.transform, "BLOCK LEADER", new Vector2(16f, -166f), new Vector2(122f, 32f), new Color(0.58f, 0.25f, 0.12f, 1f), _controller.BlockQueueLeader);
            CreateButton(_queueSettings.transform, "UNBLOCK", new Vector2(143f, -166f), new Vector2(94f, 32f), new Color(0.16f, 0.45f, 0.28f, 1f), _controller.UnblockQueueLeader);
            Button visibilityButton = CreateButton(_queueSettings.transform, "QUEUE ON", new Vector2(242f, -166f), new Vector2(162f, 32f), DARK_BUTTON_COLOR, _controller.ToggleQueueVisibility);
            _queueVisibilityLabel = visibilityButton.GetComponentInChildren<Text>(true);
        }

        private GameObject CreateSettingsGroup(Transform parent, string name)
        {
            GameObject group = new GameObject(name, typeof(RectTransform));
            group.transform.SetParent(parent, false);
            group.TryGetComponent(out RectTransform rect);
            ConfigureRect(rect, Vector2.zero, new Vector2(BOARD_WIDTH, BOARD_HEIGHT));
            group.SetActive(false);
            return group;
        }

        private void RefreshPresentation()
        {
            int lane = (int)_controller.ActiveLane;
            if (lane != _lastLane)
            {
                _lastLane = lane;
                _normalSettings.SetActive(lane == (int)TransformPathShowcaseLane.Normal);
                _multiSettings.SetActive(lane == (int)TransformPathShowcaseLane.MultiPath);
                _queueSettings.SetActive(lane == (int)TransformPathShowcaseLane.Queue);

                _activeLaneText.text = GetLaneTitle(_controller.ActiveLane);
                _activeLaneText.color = GetLaneColor(_controller.ActiveLane);
            }

            _statusText.text = _controller.GetActiveStatusText();
            if (_pauseButtonLabel != null)
                _pauseButtonLabel.text = _controller.IsPaused ? "RESUME" : "PAUSE";
            if (_queueVisibilityLabel != null)
                _queueVisibilityLabel.text = _controller.QueueVisible ? "QUEUE ON" : "QUEUE OFF";

            for (int i = 0; i < _laneButtonImages.Count; i++)
            {
                Color laneColor = GetLaneColor((TransformPathShowcaseLane)i);
                _laneButtonImages[i].color = i == lane
                    ? Color.Lerp(laneColor, Color.white, 0.25f)
                    : new Color(0.15f, 0.22f, 0.32f, 1f);
            }
        }

        private static string GetLaneTitle(TransformPathShowcaseLane lane)
        {
            switch (lane)
            {
                case TransformPathShowcaseLane.MultiPath:
                    return "ACTIVE / MULTI PATH";
                case TransformPathShowcaseLane.Queue:
                    return "ACTIVE / QUEUED PATH";
                default:
                    return "ACTIVE / NORMAL PATH";
            }
        }

        private static Color GetLaneColor(TransformPathShowcaseLane lane)
        {
            switch (lane)
            {
                case TransformPathShowcaseLane.MultiPath:
                    return MULTI_COLOR_A;
                case TransformPathShowcaseLane.Queue:
                    return QUEUE_COLOR;
                default:
                    return NORMAL_COLOR;
            }
        }

        private void EnsureEventSystem()
        {
            if (EventSystem.current != null)
                return;

            GameObject eventSystemObject = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            eventSystemObject.transform.SetParent(transform, false);
        }

        private Button CreateButton(Transform parent, string label, Vector2 position, Vector2 size, Color color, Action action)
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
            if (action != null)
                button.onClick.AddListener(() => action());

            CreateText(buttonObject.transform, "Label", label, Vector2.zero, size, 10, Color.white, TextAnchor.MiddleCenter);
            return button;
        }

        private Text CreateText(Transform parent, string name, string value, Vector2 position, Vector2 size, int fontSize, Color color, TextAnchor anchor)
        {
            GameObject textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);
            textObject.TryGetComponent(out Text text);
            text.font = _font != null ? _font : Resources.GetBuiltinResource<Font>("Arial.ttf");
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
            text.font = _font != null ? _font : Resources.GetBuiltinResource<Font>("Arial.ttf");
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
