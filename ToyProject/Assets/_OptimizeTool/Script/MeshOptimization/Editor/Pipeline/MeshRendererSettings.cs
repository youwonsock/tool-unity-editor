using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Common.OptimizeTool.MeshOptimization
{
    /// <summary>Renderer state supported by a combined output. Unsupported per-source state is rejected.</summary>
    public sealed class MeshRendererSettings
    {
        private readonly int _layer, _sortingLayer, _sortingOrder;
        private readonly uint _renderingLayers;
        private readonly ShadowCastingMode _shadows;
        private readonly bool _receiveShadows, _occlusion;
        private readonly LightProbeUsage _lightProbes;
        private readonly ReflectionProbeUsage _reflectionProbes;
        private readonly Transform _probeAnchor;
        private readonly GameObject _proxyVolume;
        private readonly MotionVectorGenerationMode _motionVectors;
        private readonly StaticEditorFlags _staticFlags;
        private readonly ReceiveGI _receiveGI;
        private readonly UnityEngine.Experimental.Rendering.RayTracingMode _rayTracing;
        private readonly int _priority;
        private readonly bool _staticShadowCaster, _stitchSeams;
        private readonly Object _lightmapParameters;
        private readonly float _scaleInLightmap;

        internal MeshRendererSettings(MeshRenderer source)
        {
            _layer = source.gameObject.layer;
            _sortingLayer = source.sortingLayerID;
            _sortingOrder = source.sortingOrder;
            _renderingLayers = source.renderingLayerMask;
            _shadows = source.shadowCastingMode;
            _receiveShadows = source.receiveShadows;
            _occlusion = source.allowOcclusionWhenDynamic;
            _lightProbes = source.lightProbeUsage;
            _reflectionProbes = source.reflectionProbeUsage;
            _probeAnchor = source.probeAnchor;
            _proxyVolume = source.lightProbeProxyVolumeOverride;
            _motionVectors = source.motionVectorGenerationMode;
            _staticFlags = GameObjectUtility.GetStaticEditorFlags(source.gameObject);
            _receiveGI = source.receiveGI;
            _rayTracing = source.rayTracingMode;
            _priority = source.rendererPriority;
            _staticShadowCaster = source.staticShadowCaster;
            _stitchSeams = source.stitchLightmapSeams;
            // Lightmap parameters and scale have no public property; they live in the serialized renderer.
            var serialized = new SerializedObject(source);
            _lightmapParameters = serialized.FindProperty(LightmapParametersProperty)?.objectReferenceValue;
            _scaleInLightmap = serialized.FindProperty(ScaleInLightmapProperty)?.floatValue ?? 1f;
        }

        private const string LightmapParametersProperty = "m_LightmapParameters";
        private const string ScaleInLightmapProperty = "m_ScaleInLightmap";

        internal static void ValidateSupported(MeshRenderer source)
        {
            if (source.HasPropertyBlock() || source.additionalVertexStreams != null
                || IsLightmapped(source.lightmapIndex) || IsLightmapped(source.realtimeLightmapIndex)
                || source.GetComponentInParent<LODGroup>() != null || source.forceRenderingOff)
                throw new ArgumentException($"'{source.name}' uses unsupported per-renderer data (PropertyBlock, vertex streams, lightmap, LOD or forceRenderingOff). Bake these separately.");
        }

        private static bool IsLightmapped(int index) => index >= 0 && index < 65534;

        /// <summary>The output will be lightmapped by a bake: it contributes to GI and receives it from lightmaps.</summary>
        internal bool UsesBakedLightmaps => (_staticFlags & StaticEditorFlags.ContributeGI) != 0 && _receiveGI == ReceiveGI.Lightmaps;

        internal void ValidateCompatible(MeshRenderer source)
        {
            ValidateSupported(source);
            var other = new MeshRendererSettings(source);
            if (_layer != other._layer || _sortingLayer != other._sortingLayer || _sortingOrder != other._sortingOrder
                || _renderingLayers != other._renderingLayers || _shadows != other._shadows
                || _receiveShadows != other._receiveShadows || _occlusion != other._occlusion
                || _lightProbes != other._lightProbes || _reflectionProbes != other._reflectionProbes
                || _probeAnchor != other._probeAnchor || _proxyVolume != other._proxyVolume
                || _motionVectors != other._motionVectors || _staticFlags != other._staticFlags || _receiveGI != other._receiveGI
                || _rayTracing != other._rayTracing || _priority != other._priority || _staticShadowCaster != other._staticShadowCaster
                || _stitchSeams != other._stitchSeams || _lightmapParameters != other._lightmapParameters
                || !Mathf.Approximately(_scaleInLightmap, other._scaleInLightmap))
                throw new ArgumentException($"'{source.name}' has incompatible renderer settings. Combine matching layers, shadows, probes, sorting, ray tracing, priority, lightmap settings and static flags separately.");
        }

        /// <summary>
        /// Assigns the output materials and the shared renderer settings. The caller assigns the mesh, because only it
        /// knows whether the transient build mesh or the saved asset belongs on the renderer.
        /// </summary>
        public void ApplyTo(MeshRenderer target, IReadOnlyList<Material> materials)
        {
            var array = new Material[materials.Count];
            for (int i = 0; i < array.Length; i++) array[i] = materials[i];
            target.sharedMaterials = array;
            target.gameObject.layer = _layer;
            target.sortingLayerID = _sortingLayer;
            target.sortingOrder = _sortingOrder;
            target.renderingLayerMask = _renderingLayers;
            target.shadowCastingMode = _shadows;
            target.receiveShadows = _receiveShadows;
            target.allowOcclusionWhenDynamic = _occlusion;
            target.lightProbeUsage = _lightProbes;
            target.reflectionProbeUsage = _reflectionProbes;
            target.probeAnchor = _probeAnchor;
            target.lightProbeProxyVolumeOverride = _proxyVolume;
            target.motionVectorGenerationMode = _motionVectors;
            target.receiveGI = _receiveGI;
            target.rayTracingMode = _rayTracing;
            target.rendererPriority = _priority;
            target.staticShadowCaster = _staticShadowCaster;
            target.stitchLightmapSeams = _stitchSeams;
            GameObjectUtility.SetStaticEditorFlags(target.gameObject, _staticFlags);
            var serialized = new SerializedObject(target);
            SerializedProperty parameters = serialized.FindProperty(LightmapParametersProperty);
            if (parameters != null) parameters.objectReferenceValue = _lightmapParameters;
            SerializedProperty scale = serialized.FindProperty(ScaleInLightmapProperty);
            if (scale != null) scale.floatValue = _scaleInLightmap;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
