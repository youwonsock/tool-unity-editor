using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// What both modes' bake assets keep the same way: the format version and
    /// Revision a bake was written with, and a content generation that tells a
    /// cached validation it may be stale after an Inspector edit or a reload.
    /// Only the FlowField assembly derives from it.
    /// </summary>
    public abstract class FlowFieldBakeAsset : ScriptableObject
    {
        // Both bake assets store these two here.
        [SerializeField] private int _formatVersion;
        [SerializeField] private int _revision;
        [NonSerialized] private int _contentGeneration;

        private protected FlowFieldBakeAsset()
        {
        }

        public int FormatVersion => _formatVersion;
        public int Revision => _revision;
        public int ContentGeneration => _contentGeneration;

        private protected abstract int CurrentFormatVersion { get; }

        private protected bool HasCurrentFormat => _formatVersion == CurrentFormatVersion;

        /// <summary>Drops what a derived asset cached from its arrays.</summary>
        private protected virtual void OnContentChanged()
        {
        }

        // Serialized edits and loads can change the arrays without Apply().
        private protected void MarkContentChanged()
        {
            unchecked { _contentGeneration++; }
            OnContentChanged();
        }

        /// <summary>Apply wrote a new bake in the current format.</summary>
        private protected void MarkBaked()
        {
            _formatVersion = CurrentFormatVersion;
            unchecked
            {
                _revision++;
                _contentGeneration++;
            }
        }

        private void OnValidate() => MarkContentChanged();

        private void OnEnable() => MarkContentChanged();

        private protected static bool Fail(out string reason, string message)
        {
            reason = message;
            return false;
        }
    }
}
