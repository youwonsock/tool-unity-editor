using System;
using UnityEngine;

namespace Common.TransformPath
{
    [Serializable]
    public sealed class PathWaypointSyncGroup
    {
        [SerializeField] private string _displayName = string.Empty;
        [SerializeField] private bool _enabled = true;
        [SerializeField] private Transform[] _members = Array.Empty<Transform>();

        public string DisplayName => _displayName;
        public bool Enabled => _enabled;
        public Transform[] Members => _members;
    }
}
