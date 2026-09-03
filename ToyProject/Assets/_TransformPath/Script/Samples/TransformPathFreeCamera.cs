using UnityEngine;
using UnityEngine.EventSystems;

namespace Common.TransformPath.Samples
{
    /// <summary>
    /// TransformPath showcase용 Perspective fly camera입니다.
    /// 우클릭 중에만 마우스 포인터를 캡처하고 이동 입력을 소비합니다.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class TransformPathFreeCamera : MonoBehaviour
    {
        [Header("Camera")]
        [SerializeField] private float _fieldOfView = 60f;
        [SerializeField] private float _nearClipPlane = 0.1f;
        [SerializeField] private float _farClipPlane = 1000f;

        [Header("Fly Controls")]
        [SerializeField] private float _lookSensitivity = 3.5f;
        [SerializeField] private float _moveSpeed = 8f;
        [SerializeField] private float _minMoveSpeed = 1f;
        [SerializeField] private float _maxMoveSpeed = 40f;
        [SerializeField] private float _scrollSpeedStep = 2f;
        [SerializeField] private float _fastMoveMultiplier = 3f;
        [SerializeField] private float _minPitch = -85f;
        [SerializeField] private float _maxPitch = 85f;

        [Header("Focus")]
        [SerializeField] private Vector3 _focusOffsetDirection = new Vector3(0f, 0.8f, -1f);
        [SerializeField] private float _focusPadding = 4f;
        [SerializeField] private float _minimumFocusDistance = 8f;

        private Camera _camera;
        private bool _isLookMode;
        private float _yaw;
        private float _pitch;

        public bool IsLookMode => _isLookMode;
        public float MoveSpeed => _moveSpeed;

        private void Awake()
        {
            TryGetComponent(out _camera);
            ConfigureCamera();
            SyncAnglesFromTransform();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
                ReleaseLookMode();

            if (!_isLookMode)
            {
                if (Input.GetMouseButtonDown(1) && !IsPointerOverUi())
                    BeginLookMode();
                return;
            }

            if (!Input.GetMouseButton(1))
            {
                ReleaseLookMode();
                return;
            }

            UpdateLookRotation();
            UpdateMoveSpeed();
            UpdateMovement();
        }

        public void FocusOnBounds(Bounds bounds)
        {
            if (_camera == null)
                TryGetComponent(out _camera);
            if (_camera == null)
                return;

            ReleaseLookMode();
            ConfigureCamera();

            Vector3 offsetDirection = _focusOffsetDirection.sqrMagnitude > 0.0001f
                ? _focusOffsetDirection.normalized
                : new Vector3(0f, 0.6f, -0.8f);
            float radius = Mathf.Max(bounds.extents.magnitude, 1f);
            float halfFovRadians = Mathf.Max(_camera.fieldOfView, 1f) * 0.5f * Mathf.Deg2Rad;
            float distance = radius / Mathf.Tan(halfFovRadians) + Mathf.Max(_focusPadding, 0f);
            distance = Mathf.Max(distance, _minimumFocusDistance);

            Vector3 target = bounds.center;
            transform.position = target + offsetDirection * distance;
            transform.LookAt(target);
            SyncAnglesFromTransform();
        }

        private void ConfigureCamera()
        {
            if (_camera == null)
                return;

            _camera.orthographic = false;
            _camera.fieldOfView = Mathf.Clamp(_fieldOfView, 20f, 120f);
            _camera.nearClipPlane = Mathf.Max(_nearClipPlane, 0.01f);
            _camera.farClipPlane = Mathf.Max(_farClipPlane, _camera.nearClipPlane + 1f);
        }

        private void BeginLookMode()
        {
            _isLookMode = true;
            SyncAnglesFromTransform();
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void ReleaseLookMode()
        {
            _isLookMode = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void UpdateLookRotation()
        {
            _yaw += Input.GetAxis("Mouse X") * _lookSensitivity;
            _pitch -= Input.GetAxis("Mouse Y") * _lookSensitivity;
            _pitch = Mathf.Clamp(_pitch, _minPitch, _maxPitch);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        private void UpdateMoveSpeed()
        {
            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) <= Mathf.Epsilon)
                return;

            _moveSpeed = Mathf.Clamp(
                _moveSpeed + scroll * _scrollSpeedStep,
                Mathf.Max(_minMoveSpeed, 0.1f),
                Mathf.Max(_maxMoveSpeed, _minMoveSpeed));
        }

        private void UpdateMovement()
        {
            float horizontal = 0f;
            if (Input.GetKey(KeyCode.A))
                horizontal -= 1f;
            if (Input.GetKey(KeyCode.D))
                horizontal += 1f;

            float forward = 0f;
            if (Input.GetKey(KeyCode.S))
                forward -= 1f;
            if (Input.GetKey(KeyCode.W))
                forward += 1f;

            float vertical = 0f;
            if (Input.GetKey(KeyCode.Q))
                vertical -= 1f;
            if (Input.GetKey(KeyCode.E))
                vertical += 1f;

            Vector3 input = new Vector3(horizontal, vertical, forward);
            if (input.sqrMagnitude <= 0.0001f)
                return;

            float speed = _moveSpeed;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                speed *= _fastMoveMultiplier;

            Quaternion yawRotation = Quaternion.Euler(0f, _yaw, 0f);
            Vector3 movement = yawRotation * input.normalized;
            transform.position += movement * speed * Time.unscaledDeltaTime;
        }

        private void SyncAnglesFromTransform()
        {
            Vector3 euler = transform.rotation.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
            _pitch = Mathf.Clamp(_pitch, _minPitch, _maxPitch);
        }

        private static bool IsPointerOverUi()
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }

        private void OnDisable()
        {
            if (_isLookMode)
                ReleaseLookMode();
        }
    }
}
