using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField.Samples
{
    /// <summary>
    /// Shared ownership and failure-cleanup boundary for the sample
    /// controllers.  A concrete sample still owns its authoring settings and
    /// goal policy; this type owns only the agent lifecycle that is identical
    /// for every sample.
    /// </summary>
    public abstract class FlowFieldSampleControllerBase : MonoBehaviour
    {
        private readonly List<FlowFieldSampleAgent> _ownedAgents =
            new List<FlowFieldSampleAgent>(64);

        /// <summary>
        /// Concrete samples may inspect their owned collection for sample
        /// specific diagnostics, but creation and disposal must go through
        /// the helpers below.
        /// </summary>
        protected List<FlowFieldSampleAgent> Agents => _ownedAgents;
        protected IReadOnlyList<FlowFieldSampleAgent> OwnedAgents => _ownedAgents;
        protected int OwnedAgentCount => _ownedAgents.Count;

        /// <summary>
        /// Takes ownership before any user/component initialization runs.  This
        /// makes a partially configured spawn recoverable from one place.
        /// </summary>
        protected void OwnAgent(FlowFieldSampleAgent agent)
        {
            if (agent == null)
                throw new ArgumentNullException(nameof(agent));
            _ownedAgents.Add(agent);
        }

        protected void ConfigureAndInitializeAgent(
            FlowFieldSampleAgent agent,
            IFlowFieldProvider provider,
            float moveSpeed,
            float maxAcceleration)
        {
            if (agent == null)
                throw new ArgumentNullException(nameof(agent));
            OwnAgentIfMissing(agent);
            agent.Configure(provider, moveSpeed, maxAcceleration);
            if (!agent.IsInitialized)
                agent.Init();
        }

        protected void SimulateOwnedAgents(float deltaTime, bool throwOnMissing = false)
        {
            for (int index = 0; index < _ownedAgents.Count; index++)
            {
                FlowFieldSampleAgent agent = _ownedAgents[index];
                if (agent == null)
                {
                    if (throwOnMissing)
                        throw new InvalidOperationException($"FlowField agent {index} is missing.");
                    continue;
                }

                if (!agent.IsInitialized)
                {
                    if (throwOnMissing)
                        throw new InvalidOperationException(
                            $"FlowField agent {index} is not initialized.");
                    continue;
                }

                agent.Simulate(deltaTime);
            }
        }

        /// <summary>
        /// Releases and destroys all owned agents.  It is intentionally
        /// idempotent so callers can use it from both failure paths and
        /// OnDestroy without having to track a second lifecycle flag.
        /// </summary>
        protected void ReleaseOwnedAgents()
        {
            for (int index = _ownedAgents.Count - 1; index >= 0; index--)
            {
                FlowFieldSampleAgent agent = _ownedAgents[index];
                if (agent == null)
                    continue;

                if (agent.IsInitialized || agent.IsFaulted)
                    agent.Release();
                if (agent.gameObject != null)
                    Destroy(agent.gameObject);
            }

            _ownedAgents.Clear();
        }

        protected void ClearOwnedAgentReferences()
            => _ownedAgents.Clear();

        private void OwnAgentIfMissing(FlowFieldSampleAgent agent)
        {
            if (!_ownedAgents.Contains(agent))
                _ownedAgents.Add(agent);
        }
    }
}
