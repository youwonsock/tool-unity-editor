using System;
using UnityEngine;

namespace Common.OptimizeTool.Samples
{
    /// <summary>Owns a validated playback instance and its manually sampled Legacy Animation.</summary>
    internal sealed class PhysicsPrefabPlayback
    {
        public GameObject Instance { get; private set; }
        private readonly Animation _animation;
        private AnimationState _state;
        public float Length => _animation.clip.length;

        private PhysicsPrefabPlayback(GameObject instance, Animation animation)
        {
            Instance = instance;
            _animation = animation;
        }

        public static PhysicsPrefabPlayback Create(GameObject prefab, Transform anchor, Rigidbody[] source)
        {
            var staging = new GameObject("Playback validation");
            staging.SetActive(false);
            staging.transform.SetParent(anchor, false);
            GameObject instance = null;
            try
            {
                instance = UnityEngine.Object.Instantiate(prefab, staging.transform, false);
                instance.name = "Recorded Prefab Playback";
                Animation animation = instance.GetComponent<Animation>();
                if (animation == null || animation.clip == null || !animation.clip.legacy
                    || float.IsNaN(animation.clip.length) || float.IsInfinity(animation.clip.length))
                    throw new InvalidOperationException("The saved recording must have a Legacy clip and no physics components.");
                PhysicsPlaybackPolicy.Validate(instance);
                instance.SetActive(false);
                animation.Stop();
                animation.playAutomatically = false;
                animation.clip.SampleAnimation(instance, 0f);
                for (int i = 0; i < source.Length; i++)
                {
                    // The sample's rigidbodies are direct children with unique names.
                    if (source[i] == null || instance.transform.Find(source[i].name) == null)
                        throw new InvalidOperationException("The recording hierarchy differs from this demo; record a new take.");
                }
                instance.transform.SetParent(anchor, false);
                instance.SetActive(true);
                return new PhysicsPrefabPlayback(instance, animation);
            }
            catch
            {
                if (instance != null)
                {
                    instance.SetActive(false);
                    UnityEngine.Object.Destroy(instance);
                }
                throw;
            }
            finally { UnityEngine.Object.Destroy(staging); }
        }

        public void Start()
        {
            if (Instance == null || _animation == null)
                throw new InvalidOperationException("The playback instance is unavailable; compare again.");
            foreach (AnimationState state in _animation)
                if (state.clip == _animation.clip) { _state = state; break; }
            if (_state == null) throw new InvalidOperationException("The saved clip has no playback state.");
            _animation.Play(_state.name);
            _state.speed = 0f;
            Sample(0f);
        }

        public void Sample(float time)
        {
            if (Instance == null || _animation == null || _state == null)
                throw new InvalidOperationException("The playback instance is unavailable; compare again.");
            _state.time = time;
            _animation.Sample();
        }

        public void Stop()
        {
            if (_animation != null) _animation.Stop();
        }

        public void Destroy()
        {
            if (Instance != null)
            {
                Instance.SetActive(false);
                UnityEngine.Object.Destroy(Instance);
            }
            Instance = null;
            _state = null;
        }
    }
}
