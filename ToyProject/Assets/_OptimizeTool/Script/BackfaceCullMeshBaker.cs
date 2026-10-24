using UnityEngine;
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
#endif

namespace Common.OptimizeTool
{
    public class BackfaceCullMeshBaker : MonoBehaviour
    {
#if UNITY_EDITOR
        [SerializeField] private string _savePath;
        [SerializeField] private MeshFilter[] _targetMeshFilterArray;
        [SerializeField] private bool _collectChildMeshes;
        [SerializeField] private bool _allowReadableCopy;
        [SerializeField] private bool _allowNormalRecalculation;
        [SerializeField] private bool _overwriteExistingAssets;
        [SerializeField] private Camera _targetCamera;
        [SerializeField] private float _backfaceCullingAngle = 90f;
        [Header("Custom Vector")]
        [SerializeField] private bool _useCustomVector;
        [SerializeField] private Vector3 _customCameraVector;

        private void ValidateSettings()
        {
            MeshProcessingUtility.RequireEditMode();
            MeshAssetTransaction.ValidateFolder(_savePath);
            if (!_useCustomVector && _targetCamera == null) throw new ArgumentException("Assign a target camera.");
            if (_useCustomVector && (!MeshProcessingUtility.IsFinite(_customCameraVector) || _customCameraVector.sqrMagnitude < 0.000001f))
                throw new ArgumentException("The custom view direction must be finite and non-zero.");
            if (_collectChildMeshes == (_targetMeshFilterArray != null && _targetMeshFilterArray.Length > 0))
                throw new ArgumentException("Choose direct MeshFilters or child collection, not both.");
            MeshProcessingUtility.ValidateAngle(_backfaceCullingAngle);
        }

        /// <summary>Stage every result before changing any asset or MeshFilter.</summary>
        public Mesh[] BakeBackFaceCullingMesh()
        {
            ValidateSettings();
            MeshFilter[] filters = _collectChildMeshes ? GetComponentsInChildren<MeshFilter>() : _targetMeshFilterArray;
            if (filters.Length == 0) throw new ArgumentException("No MeshFilters were supplied.");
            var staged = new List<Mesh>();
            var paths = new List<string>();
            try
            {
                foreach (MeshFilter filter in filters)
                {
                    if (filter == null) throw new ArgumentException("A target MeshFilter is missing.");
                    Mesh readable = MeshProcessingUtility.ReadableCopy(filter.sharedMesh, _allowReadableCopy, _allowNormalRecalculation);
                    try
                    {
                        Mesh culled = MeshProcessingUtility.CullBackfaces(readable, filter.transform, _targetCamera,
                            _backfaceCullingAngle, _useCustomVector, _customCameraVector);
                        if (culled == null) throw new InvalidOperationException($"All triangles were removed from '{filter.name}'.");
                        staged.Add(culled);
                        paths.Add($"{_savePath}/{filter.name}_BackfaceCulled.asset");
                    }
                    finally { DestroyImmediate(readable); }
                }
                Mesh[] saved = MeshAssetTransaction.Save(staged, paths, _overwriteExistingAssets, "Bake backfaces", meshes =>
                {
                    for (int i = 0; i < filters.Length; i++)
                    {
                        Undo.RegisterCompleteObjectUndo(filters[i], "Assign baked mesh");
                        filters[i].sharedMesh = meshes[i];
                        PrefabUtility.RecordPrefabInstancePropertyModifications(filters[i]);
                    }
                });
                return saved;
            }
            finally { foreach (Mesh mesh in staged) if (mesh != null) DestroyImmediate(mesh); }
        }
#endif
    }
}
