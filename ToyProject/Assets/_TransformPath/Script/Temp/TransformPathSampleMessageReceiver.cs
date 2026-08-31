using System;
using System.Collections;
using UnityEngine;

namespace Common.TransformPath.Samples
{
    /// <summary>
    /// PathEventHandler가 전달한 이벤트 메시지를 수신하는 테스트용 Receiver입니다.
    /// </summary>
    public sealed class TransformPathSampleMessageReceiver : MonoBehaviour, IPathEventReceiver
    {
        [Header("Visual Feedback")]
        [SerializeField] private Renderer _actorRenderer;
        [SerializeField] private Color _eventColor = Color.yellow;
        [SerializeField] private float _flashDuration = 0.5f;
        [SerializeField] private bool _showOverlay = true;

        private Material _actorMaterial;
        private Color _baseColor;
        private Coroutine _flashCoroutine;
        private bool _isInitialized;
        private bool _isFaulted;
        private Exception _fault;

        public string LastMessage { get; private set; } = "(waiting for path event)";
        public int ReceivedCount { get; private set; }
        public IPathFollower LastFollower { get; private set; }
        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("TransformPathSampleMessageReceiver is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("TransformPathSampleMessageReceiver is faulted; call Release before Init.", _fault);
            try
            {
                if (_actorRenderer == null)
                    throw new InvalidOperationException("Message receiver requires a serialized actor Renderer.");
                if (float.IsNaN(_flashDuration) || float.IsInfinity(_flashDuration) || _flashDuration < 0f)
                    throw new ArgumentOutOfRangeException(nameof(_flashDuration));

                _actorMaterial = _actorRenderer.material;
                if (_actorMaterial == null)
                    throw new InvalidOperationException("Message receiver actor Renderer must expose a material.");
                _baseColor = _actorMaterial.color;
                _isInitialized = true;
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

        public void ReceivePathEvent(string eventName, IPathFollower follower)
        {
            if (_isFaulted)
                throw new InvalidOperationException("Message receiver is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("Message receiver is not initialized.");
            if (eventName == null)
                throw new ArgumentNullException(nameof(eventName));
            if (eventName.Length == 0)
                throw new ArgumentException("Path event name is required.", nameof(eventName));
            if (follower == null)
                throw new ArgumentNullException(nameof(follower));
            LastMessage = eventName;
            LastFollower = follower;
            ReceivedCount++;

            Component followerComponent = follower as Component;
            string followerName = followerComponent != null
                ? followerComponent.name
                : follower.GetType().Name;
            Debug.Log(
                $"TransformPath message received: {LastMessage} from {followerName} (#{ReceivedCount})",
                this);

            if (_flashCoroutine != null)
                StopCoroutine(_flashCoroutine);

            _flashCoroutine = StartCoroutine(FlashEventColor());
        }

        private IEnumerator FlashEventColor()
        {
            _actorMaterial.color = _eventColor;

            if (_flashDuration > 0f)
                yield return new WaitForSeconds(_flashDuration);

            _actorMaterial.color = _baseColor;
            _flashCoroutine = null;
        }

        private void OnGUI()
        {
            if (!_showOverlay)
                return;

            GUI.Box(new Rect(16f, 140f, 400f, 96f), "TransformPath Message Receiver");
            GUI.Label(new Rect(30f, 168f, 370f, 22f), $"Last message: {LastMessage}");
            GUI.Label(new Rect(30f, 190f, 370f, 22f), $"Received: {ReceivedCount}");
        }

        private void OnDisable()
        {
            if (_flashCoroutine != null)
            {
                StopCoroutine(_flashCoroutine);
                _flashCoroutine = null;
            }

            if (_actorMaterial != null)
                _actorMaterial.color = _baseColor;
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
        }

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("Message receiver has not been initialized.");
            if (_flashCoroutine != null)
            {
                StopCoroutine(_flashCoroutine);
                _flashCoroutine = null;
            }
            if (_actorMaterial != null)
                _actorMaterial.color = _baseColor;
            _actorMaterial = null;
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
        }
    }
}
