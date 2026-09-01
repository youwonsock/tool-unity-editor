using System;
using UnityEngine;

namespace Common.FlowField.Samples
{
    /// <summary>
    /// FlowField 방향을 Rigidbody 물리 조향으로 적용하는 샘플 Agent입니다.
    /// 군중 샘플에서는 FlowFieldSampleController가 FixedUpdate에서 일괄 호출합니다.
    /// </summary>
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
    public sealed class FlowFieldSampleAgent : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float _moveSpeed = 3f;
        [SerializeField] private float _maxAcceleration = 8f;
        [SerializeField] private float _brakingAcceleration = 12f;

        private Rigidbody _rigidbody;
        private IFlowFieldProvider _provider;
        private bool _isInitialized;
        private bool _isFaulted;
        private Exception _fault;

        public Rigidbody Rigidbody => _rigidbody;
        public bool IsInitialized => _isInitialized;
        public bool IsFaulted => _isFaulted;
        public Vector3 Position => _rigidbody != null
            ? GetPosition()
            : throw new InvalidOperationException("FlowFieldSampleAgent requires a Rigidbody.");
        public Vector3 Velocity => _rigidbody != null
            ? GetVelocity()
            : throw new InvalidOperationException("FlowFieldSampleAgent requires a Rigidbody.");
        public bool IsFlowReady => _provider != null && _provider.IsInitialized && _provider.IsReady;

        private void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();
            // Prefab instances are configured by FlowFieldSampleController while their
            // parent is inactive. Awake can run before Configure, so only auto-init when
            // a provider was already serialized; the controller performs explicit Init
            // after Configure for newly spawned agents.
            if (Application.isPlaying && _provider != null && !_isInitialized)
                Init();
        }

        private void OnValidate()
        {
        }

        private void OnDestroy()
        {
            if (_isInitialized || _isFaulted)
                Release();
        }

        public void Configure(IFlowFieldProvider provider, float moveSpeed, float maxAcceleration)
        {
            if (_isInitialized)
                throw new InvalidOperationException("FlowFieldSampleAgent cannot be configured after Init.");
            if (_isFaulted)
                throw new InvalidOperationException("FlowFieldSampleAgent is faulted; call Release before Configure.", _fault);
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));
            if (!IsFinite(moveSpeed) || moveSpeed <= 0f)
                throw new ArgumentOutOfRangeException(nameof(moveSpeed));
            if (!IsFinite(maxAcceleration) || maxAcceleration <= 0f)
                throw new ArgumentOutOfRangeException(nameof(maxAcceleration));
            _provider = provider;
            _moveSpeed = moveSpeed;
            _maxAcceleration = maxAcceleration;
        }

        public void Init()
        {
            if (_isInitialized)
                throw new InvalidOperationException("FlowFieldSampleAgent is already initialized.");
            if (_isFaulted)
                throw new InvalidOperationException("FlowFieldSampleAgent is faulted; call Release before Init.", _fault);
            try
            {
                if (_provider == null)
                    throw new InvalidOperationException("FlowFieldSampleAgent requires a configured FlowField provider.");
                if (_rigidbody == null)
                    _rigidbody = GetComponent<Rigidbody>();
                if (_rigidbody == null)
                    throw new InvalidOperationException("FlowFieldSampleAgent requires a Rigidbody.");
                if (!IsFinite(_brakingAcceleration) || _brakingAcceleration <= 0f)
                    throw new ArgumentOutOfRangeException(nameof(_brakingAcceleration));
                ConfigurePhysics();
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

        public void Release()
        {
            if (!_isInitialized && !_isFaulted)
                throw new InvalidOperationException("FlowFieldSampleAgent has not been initialized.");
            _isInitialized = false;
            _isFaulted = false;
            _fault = null;
            _provider = null;
        }

        private Vector3 GetPosition()
        {
            if (_isFaulted)
                throw new InvalidOperationException("FlowFieldSampleAgent is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("FlowFieldSampleAgent is not initialized.");
            return _rigidbody.position;
        }

        private Vector3 GetVelocity()
        {
            if (_isFaulted)
                throw new InvalidOperationException("FlowFieldSampleAgent is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("FlowFieldSampleAgent is not initialized.");
            return _rigidbody.velocity;
        }

        internal void Simulate(float deltaTime)
        {
            if (_isFaulted)
                throw new InvalidOperationException("FlowFieldSampleAgent is faulted; call Release before use.", _fault);
            if (!_isInitialized)
                throw new InvalidOperationException("FlowFieldSampleAgent is not initialized.");
            if (!IsFinite(deltaTime) || deltaTime <= 0f)
                throw new ArgumentOutOfRangeException(nameof(deltaTime));

            if (!_provider.IsInitialized || !_provider.IsReady)
                throw new InvalidOperationException("FlowField provider is not ready.");

            FlowFieldSample sample = _provider.Sample(Position);
            Vector3 desiredVelocity = Vector3.zero;
            if (sample.HasSurface && sample.Direction.sqrMagnitude > 0.0001f)
            {
                Vector3 direction = Vector3.ProjectOnPlane(sample.Direction, sample.SurfaceNormal);
                if (direction.sqrMagnitude > 0.0001f)
                {
                    direction.Normalize();
                    desiredVelocity = direction * (_moveSpeed * sample.SpeedMultiplier);
                }
            }

            Vector3 currentVelocity = _rigidbody.velocity;
            currentVelocity.y = 0f;
            desiredVelocity.y = 0f;

            Vector3 velocityDelta = desiredVelocity - currentVelocity;
            float acceleration = desiredVelocity.sqrMagnitude > 0.0001f
                ? _maxAcceleration
                : _brakingAcceleration;
            velocityDelta = Vector3.ClampMagnitude(velocityDelta, acceleration * deltaTime);
            _rigidbody.AddForce(velocityDelta, ForceMode.VelocityChange);
        }

        private void ConfigurePhysics()
        {
            if (_rigidbody == null)
                throw new InvalidOperationException("FlowFieldSampleAgent requires a Rigidbody.");

            CapsuleCollider capsule = GetComponent<CapsuleCollider>();
            if (capsule == null)
                throw new InvalidOperationException("FlowFieldSampleAgent requires a CapsuleCollider.");
            if (capsule.sharedMaterial == null)
                throw new InvalidOperationException("FlowFieldSampleAgent requires a shared zero-friction PhysicMaterial.");
            if (!Mathf.Approximately(capsule.sharedMaterial.dynamicFriction, 0f)
                || !Mathf.Approximately(capsule.sharedMaterial.staticFriction, 0f)
                || !Mathf.Approximately(capsule.sharedMaterial.bounciness, 0f))
                throw new ArgumentException("FlowFieldSampleAgent PhysicMaterial must have zero friction and zero bounciness.", nameof(capsule.sharedMaterial));
            capsule.radius = 0.25f;
            capsule.height = 0.8f;
            capsule.direction = 1;

            _rigidbody.isKinematic = false;
            _rigidbody.useGravity = false;
            _rigidbody.drag = 0f;
            _rigidbody.angularDrag = 0f;
            _rigidbody.interpolation = RigidbodyInterpolation.None;
            _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            _rigidbody.constraints = RigidbodyConstraints.FreezePositionY
                | RigidbodyConstraints.FreezeRotation;
            _rigidbody.solverIterations = 8;
            _rigidbody.solverVelocityIterations = 2;
            _rigidbody.maxDepenetrationVelocity = 10f;
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
