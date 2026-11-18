using System;
using UnityEngine;

namespace Common.OptimizeTool.Samples
{
    /// <summary>Prebaked scale cases. No geometry generation or physics runs during measurement.</summary>
    public sealed class OptimizeToolScaleWorkload : MonoBehaviour
    {
        [Serializable]
        public sealed class ScaleCase
        {
            public int cubeCount;
            public int faceSubdivisions = 1;
            public float orthographicSize;
            public GameObject[] modes;
            public int[] rendererCounts;
            public int[] vertexCounts;
            public long[] triangleCounts;
        }

        [SerializeField] private Camera _camera;
        [SerializeField] private ScaleCase[] _cases;
        [SerializeField] private Material _material;
        public int ActiveCase { get; private set; }
        public int ActiveMode { get; private set; }
        public int CaseCount => _cases?.Length ?? 0;
        public ScaleCase Current => _cases[ActiveCase];
        public Material Material => _material;
        public Camera Camera => _camera;

        public void Configure(Camera camera, ScaleCase[] cases, Material material)
        {
            _camera = camera; _cases = cases; _material = material;
            SelectCase(0);
        }

        private void Awake()
        {
            if (_camera == null || _material == null || CaseCount == 0)
                throw new InvalidOperationException("Rebuild the scale benchmark scene.");
            SelectCase(0);
        }

        public void SelectCubeCount(int count)
        {
            for (int i = 0; i < CaseCount; i++)
                if (_cases[i].cubeCount == count) { SelectCase(i); return; }
            throw new ArgumentException("No scale case has " + count + " source cubes.");
        }

        public void SelectCase(int index)
        {
            if (index < 0 || index >= CaseCount) throw new ArgumentOutOfRangeException(nameof(index));
            for (int c = 0; c < CaseCount; c++)
            {
                if (_cases[c].modes == null || _cases[c].modes.Length != OptimizeToolSampleModes.MeshCount)
                    throw new InvalidOperationException("Each scale needs all four mesh modes.");
                for (int m = 0; m < _cases[c].modes.Length; m++) _cases[c].modes[m].SetActive(c == index && m == 0);
            }
            ActiveCase = index; ActiveMode = 0;
            _camera.orthographic = true; _camera.orthographicSize = Current.orthographicSize;
            _camera.transform.SetPositionAndRotation(new Vector3(0f, 0f, -50f), Quaternion.identity);
        }

        public void SelectMode(int mode)
        {
            if (mode < 0 || mode >= OptimizeToolSampleModes.MeshCount) throw new ArgumentOutOfRangeException(nameof(mode));
            for (int i = 0; i < Current.modes.Length; i++) Current.modes[i].SetActive(i == mode);
            ActiveMode = mode;
        }

        private void Update()
        {
            for (int mode = 0; mode < OptimizeToolSampleModes.MeshCount; mode++)
                if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + mode))) SelectMode(mode);
            if (Input.GetKeyDown(KeyCode.Q)) SelectCase((ActiveCase + CaseCount - 1) % CaseCount);
            if (Input.GetKeyDown(KeyCode.E)) SelectCase((ActiveCase + 1) % CaseCount);
        }

        private void OnGUI()
        {
            GUI.Box(new Rect(16, 16, 540, 100), "OptimizeTool Scale Benchmark");
            GUI.Label(new Rect(30, 42, 510, 24), $"{Current.cubeCount:N0} cubes | grid {Mathf.Max(1, Current.faceSubdivisions)} | {OptimizeToolSampleModes.All[ActiveMode].Name}");
            GUI.Label(new Rect(30, 66, 510, 24), $"Renderers {Current.rendererCounts[ActiveMode]:N0} | Triangles {Current.triangleCounts[ActiveMode]:N0}");
            GUI.Label(new Rect(30, 90, 510, 24), "1-4: mode   Q/E: scale   UI is disabled during benchmark capture");
        }
    }
}
