using System.Collections.Generic;
using UnityEngine;

namespace Common.OptimizeTool.MeshOptimization
{
    /// <summary>
    /// Backface bake settings and the meshes a bake replaced. A bake saves, for each target MeshFilter, a copy of its
    /// mesh that keeps only the triangles facing the view and assigns it in place. Baking runs in the editor:
    /// <c>BakeBackfaces()</c> and <c>RestoreOriginalMeshes()</c> are extension methods of the editor assembly.
    /// </summary>
    public class BackfaceCullMeshBaker : MonoBehaviour
    {
        [Tooltip("Folder under Assets/ where each baked mesh is saved as <MeshFilter name>_BackfaceCulled.asset.")]
        [SerializeField] private string _savePath;
        [Tooltip("MeshFilters to bake. Leave empty when collecting child meshes.")]
        [SerializeField] private MeshFilter[] _targetMeshFilterArray;
        [Tooltip("Bake the MeshFilters on active objects under this object instead of the assigned list.")]
        [SerializeField] private bool _collectChildMeshes;
        [Tooltip("Copy meshes whose Read/Write is off instead of failing. The importer is not changed.")]
        [SerializeField] private bool _allowReadableCopy;
        [Tooltip("Recalculate normals of meshes that have none instead of failing.")]
        [SerializeField] private bool _allowNormalRecalculation;
        [Tooltip("Replace other existing mesh assets at the output paths, keeping their GUIDs. This baker's own outputs are always replaced.")]
        [SerializeField] private bool _overwriteExistingAssets;
        [Tooltip("View the kept triangles face. When empty, the scene's enabled MainCamera, or else its first enabled camera, is used. Ignored with a custom vector.")]
        [SerializeField] private Camera _targetCamera;
        [Tooltip("Largest angle, in degrees, between a triangle normal and the direction to the view that is kept.")]
        [SerializeField] private float _backfaceCullingAngle = 90f;
        [Header("Custom Vector")]
        [Tooltip("Use a fixed view direction instead of a camera.")]
        [SerializeField] private bool _useCustomVector;
        [Tooltip("World direction the view looks along. It must be finite and non-zero.")]
        [SerializeField] private Vector3 _customCameraVector;
        [SerializeField, HideInInspector] private List<BakedMeshOriginal> _originalMeshes = new List<BakedMeshOriginal>();
        [SerializeField, HideInInspector] private List<Mesh> _bakedMeshes = new List<Mesh>();

        internal string SavePath => _savePath;
        internal MeshFilter[] TargetMeshFilters => _targetMeshFilterArray;
        internal bool CollectChildMeshes => _collectChildMeshes;
        internal bool AllowReadableCopy => _allowReadableCopy;
        internal bool AllowNormalRecalculation => _allowNormalRecalculation;
        internal bool OverwriteExistingAssets => _overwriteExistingAssets;
        internal Camera TargetCamera => _targetCamera;
        internal float BackfaceCullingAngle => _backfaceCullingAngle;
        internal bool UseCustomVector => _useCustomVector;
        internal Vector3 CustomCameraVector => _customCameraVector;

        /// <summary>For every MeshFilter this baker changed, its original mesh and the baked mesh it assigned.</summary>
        internal List<BakedMeshOriginal> OriginalMeshes
        {
            get => _originalMeshes;
            set => _originalMeshes = value;
        }

        /// <summary>Every mesh asset this baker saved; a later bake replaces them without the overwrite setting.</summary>
        internal List<Mesh> BakedMeshes
        {
            get => _bakedMeshes;
            set => _bakedMeshes = value;
        }
    }
}
