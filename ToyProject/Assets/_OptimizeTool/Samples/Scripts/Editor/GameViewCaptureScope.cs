#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Common.OptimizeTool.Samples.Editor
{
    /// <summary>Accesses Unity 2023.2's internal Game View API and restores the captured view settings.</summary>
    internal sealed class GameViewCaptureScope : IDisposable
    {
        private readonly EditorWindow _view;
        private readonly PropertyInfo _selection;
        private readonly int _oldSelection;
        private readonly bool _oldMaximized;
        private bool _disposed;

        public GameViewCaptureScope(int width, int height)
        {
            var assembly = typeof(UnityEditor.Editor).Assembly;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type viewType = assembly.GetType("UnityEditor.GameView", true);
            _view = EditorWindow.GetWindow(viewType);
            _selection = viewType.GetProperty("selectedSizeIndex", flags)
                ?? throw new NotSupportedException("This Unity version does not expose Game View size selection.");
            _oldSelection = (int)_selection.GetValue(_view);
            _oldMaximized = _view.maximized;
            try
            {
                Type sizesType = assembly.GetType("UnityEditor.GameViewSizes", true);
                Type singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
                object sizes = singleton.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy).GetValue(null);
                object group = sizesType.GetProperty("currentGroup", flags).GetValue(sizes);
                Type groupType = group.GetType();
                int count = (int)groupType.GetMethod("GetTotalCount").Invoke(group, null);
                int selected = -1;
                for (int i = 0; i < count; i++)
                {
                    object size = groupType.GetMethod("GetGameViewSize").Invoke(group, new object[] { i });
                    Type t = size.GetType();
                    if ((int)t.GetProperty("width", flags).GetValue(size) == width
                        && (int)t.GetProperty("height", flags).GetValue(size) == height)
                    { selected = i; break; }
                }
                if (selected < 0)
                {
                    Type sizeType = assembly.GetType("UnityEditor.GameViewSize", true);
                    object kind = Enum.Parse(assembly.GetType("UnityEditor.GameViewSizeType", true), "FixedResolution");
                    object size = Activator.CreateInstance(sizeType, new object[] { kind, width, height, "OptimizeTool Benchmark" });
                    groupType.GetMethod("AddCustomSize").Invoke(group, new[] { size });
                    selected = count;
                }
                _selection.SetValue(_view, selected);
                _view.maximized = true;
                _view.Focus();
                AssemblyReloadEvents.beforeAssemblyReload += Dispose;
                EditorApplication.playModeStateChanged += OnPlayModeChanged;
            }
            catch { Dispose(); throw; }
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode) Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            if (_view == null) return;
            try { _view.maximized = _oldMaximized; }
            finally { _selection.SetValue(_view, _oldSelection); }
        }
    }
}
#endif
