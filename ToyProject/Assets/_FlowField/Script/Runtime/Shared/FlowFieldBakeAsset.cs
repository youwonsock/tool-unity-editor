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
        // Both bake assets store these here.
        [SerializeField] private int _formatVersion;
        [SerializeField] private int _revision;
        // The Manager that baked it, as its GlobalObjectId string. Only the
        // Editor bake reads and writes it, so a duplicated Manager bakes into
        // its own asset instead of this one.
        [SerializeField] private string _ownerId = string.Empty;
        [NonSerialized] private int _contentGeneration;

        private protected FlowFieldBakeAsset()
        {
        }

        /// <summary>The format version the asset was baked with; see <see cref="HasCurrentFormat"/>.</summary>
        public int FormatVersion => _formatVersion;
        /// <summary>Advances with each bake written to the asset. Serialized.</summary>
        public int Revision => _revision;
        /// <summary>
        /// Advances whenever the serialized content may have changed: a bake, an Inspector edit or
        /// a load. Not serialized.
        /// </summary>
        public int ContentGeneration => _contentGeneration;
        internal string OwnerId => _ownerId ?? string.Empty;

        internal void SetOwner(string ownerId) => _ownerId = ownerId ?? string.Empty;

        private protected abstract int CurrentFormatVersion { get; }

        /// <summary>
        /// Whether the asset was baked in the format this FlowField reads, the
        /// derived type's SupportedFormatVersion. False means bake it again.
        /// </summary>
        public bool HasCurrentFormat => _formatVersion == CurrentFormatVersion;

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
