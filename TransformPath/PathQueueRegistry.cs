using System;
using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.TransformPath
{
    /// <summary>
    /// Concrete 타입을 알지 못하는 Queue Agent의 등록 순서와 정렬을 관리합니다.
    /// </summary>
    internal sealed class PathQueueRegistry
    {
        private readonly List<IQueuedPathAgent> _agents = new List<IQueuedPathAgent>();
        private readonly Dictionary<IQueuedPathAgent, int> _indices = new Dictionary<IQueuedPathAgent, int>();
        private bool _needsSort = false;

        public int Count
        {
            get
            {
                EnsureReady();
                return _agents.Count;
            }
        }

        public void Register(IQueuedPathAgent agent)
        {
            if (!IsAlive(agent) || _indices.ContainsKey(agent))
                return;

            _indices[agent] = _agents.Count;
            _agents.Add(agent);
            _needsSort = true;
        }

        public void Unregister(IQueuedPathAgent agent)
        {
            if (agent == null || !_indices.TryGetValue(agent, out int index))
                return;

            RemoveAt(index);
        }

        public IQueuedPathAgent GetAhead(IQueuedPathAgent agent)
        {
            if (!IsAlive(agent))
                return null;

            EnsureReady();
            if (!_indices.TryGetValue(agent, out int index) || index <= 0)
                return null;

            return _agents[index - 1];
        }

        public void NotifySortNeeded()
        {
            _needsSort = true;
        }

        public void Clear()
        {
            _agents.Clear();
            _indices.Clear();
            _needsSort = false;
        }

        private void EnsureReady()
        {
            PruneDeadAgents();

            if (!_needsSort)
                return;

            _agents.Sort(CompareAgentsByGlobalTimeDesc);
            RebuildIndices();
            _needsSort = false;
        }

        private void PruneDeadAgents()
        {
            for (int i = _agents.Count - 1; i >= 0; i--)
            {
                if (IsAlive(_agents[i]))
                    continue;

                RemoveAt(i);
            }
        }

        private void RemoveAt(int index)
        {
            int lastIndex = _agents.Count - 1;
            IQueuedPathAgent removed = _agents[index];

            if (index < lastIndex)
            {
                IQueuedPathAgent last = _agents[lastIndex];
                _agents[index] = last;
                _indices[last] = index;
            }

            _agents.RemoveAt(lastIndex);
            _indices.Remove(removed);
            _needsSort = true;
        }

        private void RebuildIndices()
        {
            _indices.Clear();

            for (int i = 0; i < _agents.Count; i++)
                _indices[_agents[i]] = i;
        }

        private static int CompareAgentsByGlobalTimeDesc(IQueuedPathAgent left, IQueuedPathAgent right)
            => right.GlobalNormalizedTime.CompareTo(left.GlobalNormalizedTime);

        private static bool IsAlive(IQueuedPathAgent agent)
        {
            if (agent == null)
                return false;

            if (agent is UnityEngine.Object unityAgent)
                return unityAgent != null;

            // 순수 C# Agent는 Unity 생명주기를 가지지 않으므로 유효한 참조로 간주합니다.
            // Unity Object 구현체는 위의 직접 타입 검사에서 파괴 여부를 판정합니다.
            return true;
        }
    }
}
