// #define DEBUG_LOG_LIGHT_MAP_CONVERSION

#if UNITY_2020_2_OR_NEWER
#define USE_HYBRID_LIGHT_MAPS
#endif

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using Hash128 = UnityEngine.Hash128;

namespace Unity.Rendering
{
    class LightMapConversionContext
    {
        [Flags]
        enum LightMappingFlags
        {
            None = 0,
            Lightmapped = 1,
            Directional = 2,
            ShadowMask = 4
        }

        struct MaterialLookupKey
        {
#if USE_HYBRID_LIGHT_MAPS
            public Material BaseMaterial;
            public LightMaps LightMaps;
            public LightMappingFlags Flags;
#endif
        }

        struct LightMapKey : IEquatable<LightMapKey>
        {
            public Hash128 ColorHash;
            public Hash128 DirectionHash;
            public Hash128 ShadowMaskHash;

            public LightMapKey(LightmapData lightmapData)
                : this(lightmapData.lightmapColor,
                    lightmapData.lightmapDir,
                    lightmapData.shadowMask)
            {
            }

            public LightMapKey(Texture2D color, Texture2D direction, Texture2D shadowMask)
            {
                ColorHash = default;
                DirectionHash = default;
                ShadowMaskHash = default;

#if UNITY_EDITOR
                // imageContentsHash only available in the editor, but this type is only used
                // during conversion, so it's only used in the editor.
                if (color != null) ColorHash = color.imageContentsHash;
                if (direction != null) DirectionHash = direction.imageContentsHash;
                if (shadowMask != null) ShadowMaskHash = shadowMask.imageContentsHash;
#endif
            }

            public bool Equals(LightMapKey other)
            {
                return ColorHash.Equals(other.ColorHash) && DirectionHash.Equals(other.DirectionHash) && ShadowMaskHash.Equals(other.ShadowMaskHash);
            }

            public override int GetHashCode()
            {
                var hash = new xxHash3.StreamingState(true);
                hash.Update(ColorHash);
                hash.Update(DirectionHash);
                hash.Update(ShadowMaskHash);
                return (int) hash.DigestHash64().x;
            }
        }

        public class LightMapReference
        {
#if USE_HYBRID_LIGHT_MAPS
            public LightMaps LightMaps;
            public int LightMapIndex;
#endif
        }

#if USE_HYBRID_LIGHT_MAPS
        private int m_NumLightMapCacheHits;
        private int m_NumLightMapCacheMisses;
        private int m_NumLightMappedMaterialCacheHits;
        private int m_NumLightMappedMaterialCacheMisses;

        private Dictionary<LightMapKey, LightMapReference> m_LightMapArrayCache;
        private Dictionary<MaterialLookupKey, Material> m_LightMappedMaterialCache = new Dictionary<MaterialLookupKey, Material>();

        private List<int> m_UsedLightmapIndices = new List<int>();
        private Dictionary<int, LightMapReference> m_LightMapReferences;

        public LightMapConversionContext()
        {
            Reset();
        }

        public void Reset()
        {
            m_LightMapArrayCache = new Dictionary<LightMapKey, LightMapReference>();
            m_LightMappedMaterialCache = new Dictionary<MaterialLookupKey, Material>();

            BeginConversion();
        }

        public void BeginConversion()
        {
            m_UsedLightmapIndices = new List<int>();
            m_LightMapReferences = new Dictionary<int, LightMapReference>();

            m_NumLightMapCacheHits = 0;
            m_NumLightMapCacheMisses = 0;
            m_NumLightMappedMaterialCacheHits = 0;
            m_NumLightMappedMaterialCacheMisses = 0;
        }

        public void EndConversion()
        {
#if DEBUG_LOG_LIGHT_MAP_CONVERSION
            Debug.Log($"Light map cache: {m_NumLightMapCacheHits} hits, {m_NumLightMapCacheMisses} misses. Light mapped material cache: {m_NumLightMappedMaterialCacheHits} hits, {m_NumLightMappedMaterialCacheMisses} misses.");
#endif
        }

        public void CollectLightMapUsage(Renderer renderer)
        {
            m_UsedLightmapIndices.Add(renderer.lightmapIndex);
        }

        // Check all light maps referenced within the current batch of converted Renderers.
        // Any references to light maps that have already been inserted into a LightMaps array
        // will be implemented by reusing the existing LightMaps object. Any leftover previously
        // unseen (or changed = content hash changed) light maps are combined into a new LightMaps array.
        public void ProcessLightMapsForConversion()
        {
            var lightmaps = LightmapSettings.lightmaps;
            var uniqueIndices = m_UsedLightmapIndices
                .Distinct()
                .OrderBy(x => x)
                .Where(x=> x >= 0 && x != 65534 && x < lightmaps.Length)
                .ToArray();

            var colors = new List<Texture2D>();
            var directions = new List<Texture2D>();
            var shadowMasks = new List<Texture2D>();
            var lightMapIndices = new List<int>();

            // Each light map reference is converted into a LightMapKey which identifies the light map
            // using the content hashes regardless of the index number. Previously encountered light maps
            // should be found from the cache even if their index number has changed. New or changed
            // light maps are placed into a new array.
            for (var i = 0; i < uniqueIndices.Length; i++)
            {
                var index = uniqueIndices[i];
                var lightmapData = lightmaps[index];
                var key = new LightMapKey(lightmapData);

                if (m_LightMapArrayCache.TryGetValue(key, out var lightMapRef))
                {
                    m_LightMapReferences[index] = lightMapRef;
                    ++m_NumLightMapCacheHits;
                }
                else
                {
                    colors.Add(lightmapData.lightmapColor);
                    directions.Add(lightmapData.lightmapDir);
                    shadowMasks.Add(lightmapData.shadowMask);
                    lightMapIndices.Add(index);
                    ++m_NumLightMapCacheMisses;
                }
            }

            if (lightMapIndices.Count > 0)
            {
#if DEBUG_LOG_LIGHT_MAP_CONVERSION
                Debug.Log($"Creating new DOTS light map array from {lightMapIndices.Count} light maps.");
#endif

                var lightMapArray = LightMaps.ConstructLightMaps(colors, directions, shadowMasks);

                for (int i = 0; i < lightMapIndices.Count; ++i)
                {
                    var lightMapRef = new LightMapReference
                    {
                        LightMaps = lightMapArray,
                        LightMapIndex = i,
                    };

                    m_LightMapReferences[lightMapIndices[i]] = lightMapRef;
                    m_LightMapArrayCache[new LightMapKey(colors[i], directions[i], shadowMasks[i])] = lightMapRef;
                }
            }
        }

        public LightMapReference GetLightMapReference(Renderer renderer)
        {
            if (m_LightMapReferences.TryGetValue(renderer.lightmapIndex, out var lightMapRef))
                return lightMapRef;
            else
                return null;
        }

        public Material GetLightMappedMaterial(Material baseMaterial, LightMapReference lightMapRef)
        {
            var flags = LightMappingFlags.Lightmapped;
            if (lightMapRef.LightMaps.hasDirections)
                flags |= LightMappingFlags.Directional;
            if (lightMapRef.LightMaps.hasShadowMask)
                flags |= LightMappingFlags.ShadowMask;

            var key = new MaterialLookupKey
            {
                BaseMaterial = baseMaterial,
                LightMaps = lightMapRef.LightMaps,
                Flags = flags
            };

            if (m_LightMappedMaterialCache.TryGetValue(key, out var lightMappedMaterial))
            {
                ++m_NumLightMappedMaterialCacheHits;
                return lightMappedMaterial;
            }
            else
            {
                ++m_NumLightMappedMaterialCacheMisses;
                lightMappedMaterial = CreateLightMappedMaterial(baseMaterial, lightMapRef.LightMaps);
                m_LightMappedMaterialCache[key] = lightMappedMaterial;
                return lightMappedMaterial;
            }
        }

        private static Material CreateLightMappedMaterial(Material material, LightMaps lightMaps)
        {
            var lightMappedMaterial = new Material(material);
            lightMappedMaterial.name = $"{lightMappedMaterial.name}_Lightmapped_";
            lightMappedMaterial.EnableKeyword("LIGHTMAP_ON");

            lightMappedMaterial.SetTexture("unity_Lightmaps", lightMaps.colors);
            lightMappedMaterial.SetTexture("unity_LightmapsInd", lightMaps.directions);
            lightMappedMaterial.SetTexture("unity_ShadowMasks", lightMaps.shadowMasks);

            if (lightMaps.hasDirections)
            {
                lightMappedMaterial.name = lightMappedMaterial.name + "_DIRLIGHTMAP";
                lightMappedMaterial.EnableKeyword("DIRLIGHTMAP_COMBINED");
            }

            if (lightMaps.hasShadowMask)
            {
                lightMappedMaterial.name = lightMappedMaterial.name + "_SHADOW_MASK";
            }

            return lightMappedMaterial;
        }
#endif
    }

    class RenderMeshConversionContext
    {
        enum StaticLightingMode
        {
            None = 0,
            LightMapped = 1,
            LightProbes = 2,
        }

        /// <summary>
        /// If true, a <see cref="Renderer"/> with only a single material will be converted into
        /// a single entity using <see cref="ConvertToSingleEntity"/>.
        ///
        /// If false, all entities will be converted using <see cref="ConvertToMultipleEntities"/>.
        /// </summary>
        public bool AttachToPrimaryEntityForSingleMaterial = true;

        private EntityManager m_DstEntityManager;
        private GameObjectConversionSystem m_ConversionSystem;
        private List<Material> m_SharedMaterials = new List<Material>(10);
        private LightMapConversionContext m_LightMapConversionContext;

        /// <summary>
        /// Construct a conversion context that operates within <see cref="conversionSystem"/> and
        /// that uses <see cref="dstEntityManager"/> to create entities.
        /// </summary>
        public RenderMeshConversionContext(
            EntityManager dstEntityManager,
            GameObjectConversionSystem conversionSystem,
            LightMapConversionContext lightMapConversionContext = null)
        {
            m_DstEntityManager = dstEntityManager;
            m_ConversionSystem = conversionSystem;

#if USE_HYBRID_LIGHT_MAPS
            m_LightMapConversionContext = lightMapConversionContext;
            m_LightMapConversionContext?.BeginConversion();
#endif
        }

        public void EndConversion()
        {
#if USE_HYBRID_LIGHT_MAPS
            m_LightMapConversionContext?.EndConversion();
#endif
        }

        public void CollectLightMapUsage(Renderer renderer)
        {
#if USE_HYBRID_LIGHT_MAPS
            Debug.Assert(m_LightMapConversionContext != null,
            "LightMapConversionContext must be set to call light mapping conversion methods.");
            m_LightMapConversionContext.CollectLightMapUsage(renderer);
#endif
        }

        public void ProcessLightMapsForConversion()
        {
#if USE_HYBRID_LIGHT_MAPS
            Debug.Assert(m_LightMapConversionContext != null,
            "LightMapConversionContext must be set to call light mapping conversion methods.");
            m_LightMapConversionContext.ProcessLightMapsForConversion();
#endif
        }

        public LightMapConversionContext.LightMapReference GetLightMapReference(Renderer renderer)
        {
#if USE_HYBRID_LIGHT_MAPS
            Debug.Assert(m_LightMapConversionContext != null,
            "LightMapConversionContext must be set to call light mapping conversion methods.");
            return m_LightMapConversionContext.GetLightMapReference(renderer);
#else
            return null;
#endif
        }

        public void Convert(
            Renderer renderer,
            Mesh mesh,
            List<Material> sharedMaterials = null,
            Transform root = null)
        {
            if (sharedMaterials == null)
            {
                renderer.GetSharedMaterials(m_SharedMaterials);
                sharedMaterials = m_SharedMaterials;
            }

            // Declare asset dependencies before any input validation, so dependency info will
            // be correct even if there is an error later.
            m_ConversionSystem.DeclareAssetDependency(renderer.gameObject, mesh);
            for (int i = 0; i < sharedMaterials.Count; ++i)
                m_ConversionSystem.DeclareAssetDependency(renderer.gameObject, sharedMaterials[i]);

            if (mesh == null || sharedMaterials.Count == 0)
            {
                Debug.LogWarning(
                    "Renderer is not converted because either the assigned mesh is null or no materials are assigned.",
                    renderer);
                return;
            }

            if (root is null) root = renderer.transform;
            //@TODO: Transform system should handle RenderMeshFlippedWindingTag automatically. This should not be the responsibility of the conversion system.
            bool flipWinding = math.determinant(root.localToWorldMatrix) < 0.0;

            var desc = new RenderMeshDescription(renderer, mesh, sharedMaterials, 0)
            {
                FlipWinding = flipWinding,
            };

            if (AttachToPrimaryEntityForSingleMaterial && sharedMaterials.Count == 1)
            {
                ConvertToSingleEntity(
                    m_DstEntityManager,
                    m_ConversionSystem,
                    desc,
                    renderer);
            }
            else
            {
                ConvertToMultipleEntities(
                    m_DstEntityManager,
                    m_ConversionSystem,
                    desc,
                    renderer,
                    sharedMaterials,
                    root);
            }
        }

        private Material ConfigureHybridStaticLighting(
            Entity entity,
            EntityManager entityManager,
            Renderer renderer,
            Material material)
        {
#if USE_HYBRID_LIGHT_MAPS
            var staticLightingMode = StaticLightingModeFromRenderer(renderer);
            var lightProbeUsage = renderer.lightProbeUsage;

            if (staticLightingMode == StaticLightingMode.LightMapped)
            {
                var lightMapRef = m_LightMapConversionContext.GetLightMapReference(renderer);

                if (lightMapRef != null)
                {
                    Material lightMappedMaterial =
                        m_LightMapConversionContext.GetLightMappedMaterial(material, lightMapRef);

                    entityManager.AddComponentData(entity,
                        new BuiltinMaterialPropertyUnity_LightmapST()
                            {Value = renderer.lightmapScaleOffset});
                    entityManager.AddComponentData(entity,
                        new BuiltinMaterialPropertyUnity_LightmapIndex() {Value = lightMapRef.LightMapIndex});
                    entityManager.AddSharedComponentData(entity, lightMapRef.LightMaps);

                    return lightMappedMaterial;
                }
            }
            else if (staticLightingMode == StaticLightingMode.LightProbes)
            {
                if (lightProbeUsage == LightProbeUsage.CustomProvided)
                    entityManager.AddComponent<CustomProbeTag>(entity);
                else if (lightProbeUsage == LightProbeUsage.BlendProbes)
                    entityManager.AddComponent<BlendProbeTag>(entity);
                else
                    entityManager.AddComponent<AmbientProbeTag>(entity);
            }
#endif

            return null;
        }

        /// <summary>
        /// Convert the given <see cref="Renderer"/> into a single Hybrid Rendered entity that uses
        /// the first <see cref="Material"/> configured in the <see cref="Renderer"/>.
        /// </summary>
        public void ConvertToSingleEntity(
            EntityManager dstEntityManager,
            GameObjectConversionSystem conversionSystem,
            RenderMeshDescription renderMeshDescription,
            Renderer renderer)
        {
            var entity = conversionSystem.GetPrimaryEntity(renderer);

            var lightmappedMaterial = ConfigureHybridStaticLighting(
                entity,
                dstEntityManager,
                renderer,
                renderMeshDescription.RenderMesh.material);

            if (lightmappedMaterial != null)
                renderMeshDescription.RenderMesh.material = lightmappedMaterial;

            RenderMeshUtility.AddComponents(
                entity,
                dstEntityManager,
                renderMeshDescription);

            conversionSystem.ConfigureEditorRenderData(entity, renderer.gameObject, true);
        }

        /// <summary>
        /// Convert the given <see cref="Renderer"/> into a multiple Hybrid Rendered entities such that every
        /// <see cref="Material"/> in the <see cref="Renderer"/> is converted into one entity that uses that <see cref="Material"/>
        /// with the corresponding sub-mesh from <see cref="RenderMesh.mesh"/>. All created entities will be parented
        /// to the entity created from <see cref="root"/>.
        /// </summary>
        public void ConvertToMultipleEntities(
            EntityManager dstEntityManager,
            GameObjectConversionSystem conversionSystem,
            RenderMeshDescription renderMeshDescription,
            Renderer renderer,
            List<Material> sharedMaterials,
            Transform root)
        {
            int materialCount = sharedMaterials.Count;

            float4x4 localToWorld = root.localToWorldMatrix;
            var rootEntity = conversionSystem.GetPrimaryEntity(root);

            for (var m = 0; m != materialCount; m++)
            {
                var meshEntity = conversionSystem.CreateAdditionalEntity(renderer);

                // required for incremental conversion to work without a dependency on the transform itself.
                dstEntityManager.AddComponent<CopyTransformFromPrimaryEntityTag>(meshEntity);
                dstEntityManager.AddComponentData(meshEntity, new LocalToWorld {Value = localToWorld});
                if (!dstEntityManager.HasComponent<Static>(meshEntity))
                {
                    dstEntityManager.AddComponentData(meshEntity, new Parent {Value = rootEntity});
                    dstEntityManager.AddComponentData(meshEntity,
                        new LocalToParent {Value = float4x4.identity});
                }

                var material = sharedMaterials[m];

                var lightmappedMaterial = ConfigureHybridStaticLighting(
                    meshEntity,
                    dstEntityManager,
                    renderer,
                    material);

                if (lightmappedMaterial != null)
                    material = lightmappedMaterial;

                renderMeshDescription.RenderMesh.subMesh  = m;
                renderMeshDescription.RenderMesh.material = material;

                RenderMeshUtility.AddComponents(
                    meshEntity,
                    dstEntityManager,
                    renderMeshDescription);

                conversionSystem.ConfigureEditorRenderData(meshEntity, renderer.gameObject, true);
            }
        }

        /// <summary>
        /// Return the <see cref="StaticLightingMode"/> that corresponds to the lighting settings of <see cref="renderer"/>.
        /// </summary>
        private static StaticLightingMode StaticLightingModeFromRenderer(Renderer renderer)
        {
            var staticLightingMode = StaticLightingMode.None;
            if (renderer.lightmapIndex >= 65534 || renderer.lightmapIndex < 0)
                staticLightingMode = StaticLightingMode.LightProbes;
            else if (renderer.lightmapIndex >= 0)
                staticLightingMode = StaticLightingMode.LightMapped;

            return staticLightingMode;
        }
    }
}
