using UnityEngine;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
using System.IO;
#endif

namespace Supercent.Common.OptimizeTool
{
    public class BackfaceCullMeshBaker : MonoBehaviour
    {
#if UNITY_EDITOR
        
        #region Constants
        
        private const string SAVE_PATH = "Assets/Supercent/BackfaceCulledMeshes/";
        private const int TRIANGLE_VERTEX_COUNT = 3;
        private const string MESH_EXTENSION = ".asset";
        private const string BACKFACE_CULLED_SUFFIX = "_BackfaceCulled";
        private const string BAKED_SUFFIX = "_Baked";
        private const string TEMP_MESH_EXTRACTOR_NAME = "TempMesh   ₩Extractor";
        private const string READABLE_PROPERTY_NAME = "m_IsReadable";
        
        #endregion


        #region Inner Classes

        private class BakedMeshData
        {
            private const int HASH_BASE = 17;
            private const int HASH_MULTIPLIER = 31;

            public Mesh Mesh { get; private set; }
            public Quaternion Rotation { get; private set; }

            public BakedMeshData(Mesh mesh, Quaternion rotation)
            {
                Mesh = mesh;
                Rotation = rotation;
            }

            public override bool Equals(object obj)
            {
                if (obj == null || GetType() != obj.GetType())
                    return false;

                BakedMeshData other = (BakedMeshData)obj;
                return Mesh == other.Mesh && Rotation == other.Rotation;
            }

            public override int GetHashCode()
            {
                int hash = HASH_BASE;
                
                if (Mesh != null)
                    hash = hash * HASH_MULTIPLIER + Mesh.GetHashCode();
                    
                hash = hash * HASH_MULTIPLIER + Rotation.GetHashCode();
                
                return hash;
            }
        }

        #endregion


        #region Member Variables

        private static Dictionary<BakedMeshData, Mesh> _bakedMeshDictionary = new Dictionary<BakedMeshData, Mesh>();
        private static Dictionary<Mesh, Mesh> _readableMeshDictionary = new Dictionary<Mesh, Mesh>();

        [SerializeField] private MeshFilter[] _targetMeshFilterArray;
        [SerializeField] private Camera _targetCamera;
        [SerializeField] private float _backfaceCullingAngle = 90f;

        [Header("Custom Vector")]
        [SerializeField] private bool _useCustomVector = false;
        [SerializeField] private Vector3 _customCameraVector = Vector3.zero;

        [Header("Save Button")]
        [SerializeField] private bool _saveButton = false;

        #endregion


        #region Unity Events & Init/Release

        private void Awake()
        {
            CreateSaveDirectory();

            if(_targetCamera == null)
                _targetCamera = Camera.main;

            if(_targetMeshFilterArray.Length <= 0)
                _targetMeshFilterArray = GetComponentsInChildren<MeshFilter>();
        }

        private void Update()
        {
            if(_saveButton)
            {
                _saveButton = false;
                BakeBackFaceCullingMesh();
            }
        }

        #endregion


        #region Private Functions

        private void BakeBackFaceCullingMesh()
        {
            MeshFilter[] meshes = _targetMeshFilterArray.Length <= 0 ? 
                GetComponentsInChildren<MeshFilter>() : _targetMeshFilterArray;

            foreach(MeshFilter meshFilter in meshes)
            {
                Mesh mesh = meshFilter.sharedMesh;

                if(mesh == null)
                    continue;

                mesh = GetReadableMesh(mesh);
                if(mesh == null)
                    continue;

                BakedMeshData bakedMeshData = new BakedMeshData(mesh, meshFilter.transform.rotation);
                if(_bakedMeshDictionary.TryGetValue(bakedMeshData, out Mesh bakedMesh))
                {
                    meshFilter.sharedMesh = bakedMesh;
                    continue;
                }

                Mesh backfaceCulledMesh = ApplyBackfaceCulling(mesh, meshFilter.transform, meshFilter.name);
                if(backfaceCulledMesh == null)
                    continue;

                SaveMeshAsAsset(backfaceCulledMesh, backfaceCulledMesh.name);
                meshFilter.sharedMesh = backfaceCulledMesh;
                _bakedMeshDictionary.Add(bakedMeshData, backfaceCulledMesh);
            }
        }

        private Mesh ApplyBackfaceCulling(Mesh baseMesh, Transform meshTransform, string meshName)
        {
            if (baseMesh == null || _targetCamera == null)
                return null;
            
            Vector3[] vertices = baseMesh.vertices;
            Vector3[] normals = baseMesh.normals;
            Vector2[] uvs = baseMesh.uv;
            Color[] colors = baseMesh.colors;
            
            if (vertices == null || vertices.Length == 0)
                return null;
            
            // 노멀이 없으면 전체 삼각형을 이용해 계산
            if (normals == null || normals.Length != vertices.Length)
                normals = CalculateNormalsFromAllSubmeshes(baseMesh, vertices);
            
            Vector3 cameraForward = _useCustomVector ? 
                -_customCameraVector.normalized : -_targetCamera.transform.forward.normalized;

            // 서브메쉬별로 처리
            int subMeshCount = baseMesh.subMeshCount;
            List<int[]> culledSubMeshTriangles = new List<int[]>();
            bool hasAnyTriangles = false;

            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                int[] triangles = baseMesh.GetTriangles(subMeshIndex);
                
                if (triangles == null || triangles.Length == 0)
                {
                    culledSubMeshTriangles.Add(new int[0]);
                    continue;
                }

                List<int> culledTriangles = ProcessSubmeshTriangles(triangles, vertices, meshTransform, cameraForward);
                culledSubMeshTriangles.Add(culledTriangles.ToArray());
                
                if (culledTriangles.Count > 0)
                    hasAnyTriangles = true;
            }
            
            if (!hasAnyTriangles)
            {
                Debug.Log($"백페이스 컬링: {baseMesh.name}, 제거된 삼각형 없음");
                return baseMesh;
            }
            
            return CreateOptimizedMeshWithSubmeshes(vertices, normals, uvs, colors, culledSubMeshTriangles, meshName);
        }

        private List<int> ProcessSubmeshTriangles(int[] triangles, Vector3[] vertices, Transform meshTransform, Vector3 cameraForward)
        {
            List<int> culledTriangles = new List<int>();
            
            // 삼각형별로 백페이스 여부 검사
            for (int i = 0; i < triangles.Length; i += TRIANGLE_VERTEX_COUNT)
            {
                int v0 = triangles[i];
                int v1 = triangles[i + 1];
                int v2 = triangles[i + 2];
                
                if (v0 >= vertices.Length || v1 >= vertices.Length || v2 >= vertices.Length)
                    continue;
                
                Vector3 worldV0 = meshTransform.TransformPoint(vertices[v0]);
                Vector3 worldV1 = meshTransform.TransformPoint(vertices[v1]);
                Vector3 worldV2 = meshTransform.TransformPoint(vertices[v2]);
                
                // 삼각형의 노멀 계산
                Vector3 triangleNormal = CalculateTriangleNormal(worldV0, worldV1, worldV2);
                
                // 노멀과 카메라 방향의 내적으로 백페이스 판단
                float dotProduct = Vector3.Dot(cameraForward, triangleNormal);
                float angle = Mathf.Acos(Mathf.Clamp(dotProduct, -1f, 1f)) * Mathf.Rad2Deg;
                
                // 각도가 설정값보다 크면 백페이스로 판단하여 제외
                if (angle <= _backfaceCullingAngle)
                {
                    culledTriangles.Add(triangles[i]);
                    culledTriangles.Add(triangles[i + 1]);
                    culledTriangles.Add(triangles[i + 2]);
                }
            }
            
            return culledTriangles;
        }

        private Mesh CreateOptimizedMeshWithSubmeshes(Vector3[] originalVertices, Vector3[] originalNormals, 
            Vector2[] originalUVs, Color[] originalColors, List<int[]> culledSubMeshTriangles, string meshName)
        {
            // 모든 서브메쉬에서 사용되는 버텍스 인덱스를 수집
            HashSet<int> usedVertices = new HashSet<int>();
            foreach (int[] subMeshTriangles in culledSubMeshTriangles)
            {
                foreach (int vertexIndex in subMeshTriangles)
                    usedVertices.Add(vertexIndex);
            }
            
            // 사용되는 버텍스만 추려내기
            Dictionary<int, int> vertexMapping = new Dictionary<int, int>();
            List<Vector3> newVertices = new List<Vector3>();
            List<Vector3> newNormals = new List<Vector3>();
            List<Vector2> newUVs = new List<Vector2>();
            List<Color> newColors = new List<Color>();
            
            foreach (int originalIndex in usedVertices)
            {
                vertexMapping[originalIndex] = newVertices.Count;
                newVertices.Add(originalVertices[originalIndex]);
                
                if (originalNormals != null && originalNormals.Length > originalIndex)
                    newNormals.Add(originalNormals[originalIndex]);
                    
                if (originalUVs != null && originalUVs.Length > originalIndex)
                    newUVs.Add(originalUVs[originalIndex]);
                    
                if (originalColors != null && originalColors.Length > originalIndex)
                    newColors.Add(originalColors[originalIndex]);
            }
            
            // 새 메쉬 생성
            Mesh culledMesh = new Mesh();
            culledMesh.name = meshName + BACKFACE_CULLED_SUFFIX;
            culledMesh.vertices = newVertices.ToArray();
            culledMesh.subMeshCount = culledSubMeshTriangles.Count;
            
            // 각 서브메쉬의 삼각형 인덱스를 새로운 버텍스 인덱스로 매핑
            for (int subMeshIndex = 0; subMeshIndex < culledSubMeshTriangles.Count; subMeshIndex++)
            {
                int[] subMeshTriangles = culledSubMeshTriangles[subMeshIndex];
                int[] remappedTriangles = new int[subMeshTriangles.Length];
                
                for (int i = 0; i < subMeshTriangles.Length; i++)
                    remappedTriangles[i] = vertexMapping[subMeshTriangles[i]];
                
                culledMesh.SetTriangles(remappedTriangles, subMeshIndex);
            }
            
            if (newNormals.Count > 0)
                culledMesh.normals = newNormals.ToArray();
            else
                culledMesh.RecalculateNormals();
                
            if (newUVs.Count > 0)
                culledMesh.uv = newUVs.ToArray();
                
            if (newColors.Count > 0)
                culledMesh.colors = newColors.ToArray();
                
            culledMesh.RecalculateBounds();
            
            // 메쉬를 readable false로 설정하여 메모리 최적화
            culledMesh.UploadMeshData(true);
            
            return culledMesh;
        }

        private Mesh GetReadableMesh(Mesh originalMesh)
        {
            if (originalMesh == null)
                return null;
                
            if(_readableMeshDictionary.TryGetValue(originalMesh, out Mesh readableMesh))
                return readableMesh;
            
            readableMesh = CreateReadableMeshFromOriginal(originalMesh);
            
            if (readableMesh != null && readableMesh.vertexCount > 0)
            {
                _readableMeshDictionary.Add(originalMesh, readableMesh);
                return readableMesh;
            }
            
            return null;
        }

        private Mesh CreateReadableMeshFromOriginal(Mesh originalMesh)
        {
            GameObject tempGO = new GameObject(TEMP_MESH_EXTRACTOR_NAME);
            MeshFilter tempMF = tempGO.AddComponent<MeshFilter>();
            
            tempMF.sharedMesh = originalMesh;
            
            // Bake 방식으로 메쉬 데이터 추출
            Mesh bakedMesh = new Mesh();
            bakedMesh.name = originalMesh.name + BAKED_SUFFIX;
            
            // SkinnedMeshRenderer를 통한 베이킹 시도
            SkinnedMeshRenderer tempSMR = tempGO.AddComponent<SkinnedMeshRenderer>();
            tempSMR.sharedMesh = originalMesh;
            tempSMR.BakeMesh(bakedMesh);

            DestroyImmediate(tempGO);
            
            if (bakedMesh.vertexCount > 0)
                return bakedMesh;
            
            Debug.Log($"배이크 된 메쉬가 없습니다 : {originalMesh.name}");
            return null;
        }

        private Vector3 CalculateTriangleNormal(Vector3 v0, Vector3 v1, Vector3 v2)
        {
            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            return Vector3.Cross(edge1, edge2).normalized;
        }
        
        private Vector3[] CalculateNormalsFromAllSubmeshes(Mesh mesh, Vector3[] vertices)
        {
            Vector3[] normals = new Vector3[vertices.Length];
            
            // 모든 서브메쉬의 삼각형을 이용해 노멀 계산
            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                int[] triangles = mesh.GetTriangles(subMeshIndex);
                if (triangles == null || triangles.Length == 0)
                    continue;
                
                // 각 삼각형의 노멀을 계산하여 버텍스에 누적
                for (int i = 0; i < triangles.Length; i += TRIANGLE_VERTEX_COUNT)
                {
                    int v0 = triangles[i];
                    int v1 = triangles[i + 1];
                    int v2 = triangles[i + 2];
                    
                    if (v0 >= vertices.Length || v1 >= vertices.Length || v2 >= vertices.Length)
                        continue;
                    
                    Vector3 triangleNormal = CalculateTriangleNormal(vertices[v0], vertices[v1], vertices[v2]);
                    
                    normals[v0] += triangleNormal;
                    normals[v1] += triangleNormal;
                    normals[v2] += triangleNormal;
                }
            }
            
            // 정규화
            for (int i = 0; i < normals.Length; i++)
                normals[i] = normals[i].normalized;
            
            return normals;
        }

        private void CreateSaveDirectory()
        {
            if (!AssetDatabase.IsValidFolder(SAVE_PATH.TrimEnd('/')))
            {
                string parentFolder = Path.GetDirectoryName(SAVE_PATH.TrimEnd('/'));
                string folderName = Path.GetFileName(SAVE_PATH.TrimEnd('/'));
                AssetDatabase.CreateFolder(parentFolder, folderName);
            }
        }

        private void SaveMeshAsAsset(Mesh mesh, string fileName)
        {
            string fullPath = SAVE_PATH + fileName + MESH_EXTENSION;
            
            // 기존 에셋이 있다면 덮어쓰기
            Mesh existingMesh = AssetDatabase.LoadAssetAtPath<Mesh>(fullPath);
            if (existingMesh != null)
            {
                existingMesh.Clear();
                EditorUtility.CopySerialized(mesh, existingMesh);
                AssetDatabase.SaveAssets();
                Debug.Log($"메쉬가 업데이트되었습니다: {fullPath}");
            }
            else
            {
                AssetDatabase.CreateAsset(mesh, fullPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"메쉬가 저장되었습니다: {fullPath}");
            }
            
            // 저장된 메쉬의 Read/Write Enabled를 false로 설정
            SetMeshReadWriteEnabled(fullPath, false);
            
            AssetDatabase.Refresh();
        }

        private void SetMeshReadWriteEnabled(string assetPath, bool enabled)
        {
            ModelImporter importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer != null)
            {
                importer.isReadable = enabled;
                importer.SaveAndReimport();
            }
            else
            {
                // 직접 생성한 메쉬 에셋의 경우 SerializedObject를 통해 설정
                Mesh meshAsset = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                if (meshAsset != null)
                {
                    SerializedObject serializedMesh = new SerializedObject(meshAsset);
                    SerializedProperty readableProperty = serializedMesh.FindProperty(READABLE_PROPERTY_NAME);
                    if (readableProperty != null)
                    {
                        readableProperty.boolValue = enabled;
                        serializedMesh.ApplyModifiedProperties();
                        EditorUtility.SetDirty(meshAsset);
                        AssetDatabase.SaveAssets();
                    }
                }
            }
        }

        #endregion

#endif
    }
}