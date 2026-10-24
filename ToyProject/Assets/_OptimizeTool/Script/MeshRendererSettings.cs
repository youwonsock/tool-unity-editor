#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Common.OptimizeTool
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
        }

        internal static void ValidateSupported(MeshRenderer source)
        {
            if (source.HasPropertyBlock() || source.additionalVertexStreams != null
                || IsLightmapped(source.lightmapIndex) || IsLightmapped(source.realtimeLightmapIndex)
                || source.GetComponentInParent<LODGroup>() != null || source.forceRenderingOff)
                throw new ArgumentException($"'{source.name}' uses unsupported per-renderer data (PropertyBlock, vertex streams, lightmap, LOD or forceRenderingOff). Bake these separately.");
        }

        private static bool IsLightmapped(int index) => index >= 0 && index < 65534;

        internal void ValidateCompatible(MeshRenderer source)
        {
            ValidateSupported(source);
            var other = new MeshRendererSettings(source);
            if (_layer != other._layer || _sortingLayer != other._sortingLayer || _sortingOrder != other._sortingOrder
                || _renderingLayers != other._renderingLayers || _shadows != other._shadows
                || _receiveShadows != other._receiveShadows || _occlusion != other._occlusion
                || _lightProbes != other._lightProbes || _reflectionProbes != other._reflectionProbes
                || _probeAnchor != other._probeAnchor || _proxyVolume != other._proxyVolume
                || _motionVectors != other._motionVectors || _staticFlags != other._staticFlags || _receiveGI != other._receiveGI)
                throw new ArgumentException($"'{source.name}' has incompatible renderer settings. Combine matching layers, shadows, probes, sorting and static flags separately.");
        }

        public void ApplyTo(MeshRenderer target)
        {
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
            GameObjectUtility.SetStaticEditorFlags(target.gameObject, _staticFlags);
        }
    }
}
#endif
