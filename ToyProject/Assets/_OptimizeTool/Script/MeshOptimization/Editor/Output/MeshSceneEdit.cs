using System;
using System.Collections.Generic;
using Common.OptimizeTool.Editor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.MeshOptimization.Editor
{
    /// <summary>
    /// How one mesh command changes the scene, decided by the Editor mode in this one place.
    /// Persistent (Edit Mode): every change is recorded with Undo and as a Prefab instance override.
    /// Temporary (Play Mode): nothing is recorded; the edit keeps what each change replaced, so a failed command puts the
    /// scene back itself, and leaving Play Mode discards the scene changes. Saved assets stay in both modes.
    /// </summary>
    internal sealed class MeshSceneEdit
    {
        private readonly List<Action> _reverts = new List<Action>();
        private readonly List<GameObject> _created = new List<GameObject>();
        private readonly List<KeyValuePair<GameObject, bool>> _removed = new List<KeyValuePair<GameObject, bool>>();
        private readonly int _undoGroup = -1;

        private MeshSceneEdit(bool persistent, string undoGroupName)
        {
            IsPersistent = persistent;
            if (!persistent || undoGroupName == null) return;
            Undo.IncrementCurrentGroup();
            _undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoGroupName);
        }

        internal bool IsPersistent { get; }

        /// <summary>True in Edit Mode, false in Play Mode. Throws while Play Mode is starting or stopping.</summary>
        internal static bool CurrentIsPersistent
        {
            get
            {
                AssetFileTransaction.RequireStablePlayMode();
                return !EditorApplication.isPlaying;
            }
        }

        /// <summary>
        /// Starts an edit in the current mode. With <paramref name="undoGroupName"/>, a Persistent edit is one Undo step
        /// of its own; without it, the edit joins the caller's group, such as the one of
        /// <see cref="MeshAssetTransaction.Save"/>, which also reverts it.
        /// </summary>
        internal static MeshSceneEdit Begin(string undoGroupName = null) => new MeshSceneEdit(CurrentIsPersistent, undoGroupName);

        /// <summary>Call before changing <paramref name="target"/>. <paramref name="revert"/> undoes a Temporary change.</summary>
        internal void Record(Object target, string undoName, Action revert)
        {
            if (IsPersistent) Undo.RegisterCompleteObjectUndo(target, undoName);
            else _reverts.Add(revert);
        }

        /// <summary>Call after changing <paramref name="target"/>, so a Persistent change of a Prefab instance is kept.</summary>
        internal void Changed(Object target)
        {
            if (IsPersistent) PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }

        internal void Created(GameObject created, string undoName)
        {
            if (IsPersistent) Undo.RegisterCreatedObjectUndo(created, undoName);
            else _created.Add(created);
        }

        /// <summary>
        /// Persistent: destroys <paramref name="target"/> with Undo. Temporary: deactivates it until <see cref="Commit"/>
        /// destroys it or <see cref="Revert"/> brings it back.
        /// </summary>
        internal void Remove(GameObject target)
        {
            if (IsPersistent)
            {
                Undo.DestroyObjectImmediate(target);
                return;
            }
            _removed.Add(new KeyValuePair<GameObject, bool>(target, target.activeSelf));
            target.SetActive(false);
        }

        /// <summary>Ends a successful command.</summary>
        internal void Commit()
        {
            if (IsPersistent)
            {
                if (_undoGroup >= 0) Undo.CollapseUndoOperations(_undoGroup);
                return;
            }
            foreach (KeyValuePair<GameObject, bool> removed in _removed)
                if (removed.Key != null) Object.DestroyImmediate(removed.Key);
            Clear();
        }

        /// <summary>
        /// Puts the scene back after a failure. Each step runs even when an earlier one fails; those failures are
        /// logged, and the caller rethrows the command's own error.
        /// </summary>
        internal void Revert()
        {
            if (IsPersistent)
            {
                if (_undoGroup >= 0) Attempt(() => Undo.RevertAllDownToGroup(_undoGroup));
                return;
            }
            for (int i = _reverts.Count - 1; i >= 0; i--) Attempt(_reverts[i]);
            foreach (GameObject created in _created)
                if (created != null) Attempt(() => Object.DestroyImmediate(created));
            foreach (KeyValuePair<GameObject, bool> removed in _removed)
                if (removed.Key != null) Attempt(() => removed.Key.SetActive(removed.Value));
            Clear();
        }

        private static void Attempt(Action step)
        {
            try { step(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private void Clear()
        {
            _reverts.Clear();
            _created.Clear();
            _removed.Clear();
        }
    }
}
