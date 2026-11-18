using UnityEngine;

namespace Common.OptimizeTool.MeshOptimization
{
    /// <summary>
    /// Backface bake settings. Baking runs in the editor: <c>BakeBackFaceCullingMesh()</c> is an extension
    /// method of the editor assembly.
    /// </summary>
    public class BackfaceCullMeshBaker : MonoBehaviour
    {
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
    }
}
