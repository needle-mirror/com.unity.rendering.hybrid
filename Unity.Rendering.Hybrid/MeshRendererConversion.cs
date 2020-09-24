using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Transforms;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Rendering
{
    [Flags]
    public enum LightMappingFlags
    {
        None = 0,
        Lightmapped = 1,
        Directional = 2,
        ShadowMask = 4
    }

    public struct MaterialLookupKey
    {
        public Material BaseMaterial;
        public LightMaps lightmaps;
        public LightMappingFlags Flags;
    }

    [ConverterVersion("unity", 10)]
    [WorldSystemFilter(WorldSystemFilterFlags.HybridGameObjectConversion)]
    class MeshRendererConversion : GameObjectConversionSystem
    {
        const bool AttachToPrimaryEntityForSingleMaterial = true;

        protected override void OnUpdate()
        {
            var globalToLocalLightmapMap = new Dictionary<int, int>();
#if UNITY_2020_2_OR_NEWER
            var usedIndicies = new List<int>();
            Entities.WithNone<TextMesh>().ForEach((MeshRenderer meshRenderer, MeshFilter meshFilter) =>
            {
                usedIndicies.Add(meshRenderer.lightmapIndex);
            });

            var setting = LightmapSettings.lightmaps;
            var uniqueIndices = usedIndicies.Distinct().OrderBy(x => x).Where(x=> x >= 0 && x != 65534 && x < setting.Length).ToArray();

            var colors = new List<Texture2D>();
            var directions = new List<Texture2D>();
            var shadowMasks = new List<Texture2D>();

            for (var i = 0; i < uniqueIndices.Length; i++)
            {
                var index = uniqueIndices[i];
                colors.Add(setting[index].lightmapColor);
                directions.Add(setting[index].lightmapDir);
                shadowMasks.Add(setting[index].shadowMask);
                globalToLocalLightmapMap.Add(index, i);
            }

            var lightMaps = LightMaps.ConstructLightMaps(colors, directions, shadowMasks);
#else
            var lightMaps = new LightMaps();
#endif
            var sourceMaterials = new List<Material>(10);
            var createdMaterials = new Dictionary<MaterialLookupKey, Material>();

            Entities.WithNone<TextMesh>().ForEach((MeshRenderer meshRenderer, MeshFilter meshFilter) =>
            {
                var mesh = meshFilter.sharedMesh;
                meshRenderer.GetSharedMaterials(sourceMaterials);

                if (!globalToLocalLightmapMap.TryGetValue(meshRenderer.lightmapIndex, out var lightmapIndex))
                    lightmapIndex = meshRenderer.lightmapIndex;

                if(mesh != null)
                    Convert(DstEntityManager, this, AttachToPrimaryEntityForSingleMaterial, meshRenderer, mesh, sourceMaterials,
                        createdMaterials, lightMaps, lightmapIndex, meshRenderer.transform, mesh.bounds.ToAABB());
            });
        }

#if ENABLE_HYBRID_RENDERER_V2
        private static BuiltinMaterialPropertyUnity_MotionVectorsParams CreateMotionVectorsParams(ref RenderMesh mesh, ref Renderer meshRenderer)
        {
            float s_bias = -0.001f;
            float hasLastPositionStream = mesh.needMotionVectorPass ? 1.0f : 0.0f;
            var motionVectorGenerationMode = meshRenderer.motionVectorGenerationMode;
            float forceNoMotion = (motionVectorGenerationMode == MotionVectorGenerationMode.ForceNoMotion) ? 0.0f : 1.0f;
            float cameraVelocity = (motionVectorGenerationMode == MotionVectorGenerationMode.Camera) ? 0.0f : 1.0f;
            return new BuiltinMaterialPropertyUnity_MotionVectorsParams { Value = new float4(hasLastPositionStream, forceNoMotion, s_bias, cameraVelocity) };
        }
#endif

        enum StaticLightingMode
        {
            None = 0,
            Lightmapped = 1,
            LightProbes = 2,
        }


        private static void AddComponentsToEntity(
            Entity entity,
            EntityManager dstEntityManager,
            GameObjectConversionSystem conversionSystem,
            Renderer meshRenderer,
            Mesh mesh,
            List<Material> materials,
            Dictionary<MaterialLookupKey, Material> createdMaterials,
            bool flipWinding,
            int id,
            LightMaps lightMaps,
            int lightmapIndex,
            AABB localBounds)
        {
            var needMotionVectorPass = (meshRenderer.motionVectorGenerationMode == MotionVectorGenerationMode.Object) ||
                                       (meshRenderer.motionVectorGenerationMode == MotionVectorGenerationMode.ForceNoMotion);

            var renderMesh = new RenderMesh
            {
                mesh = mesh,
                castShadows = meshRenderer.shadowCastingMode,
                receiveShadows = meshRenderer.receiveShadows,
                layer = meshRenderer.gameObject.layer,
                subMesh = id,
                needMotionVectorPass = needMotionVectorPass
            };

            var staticLightingMode = StaticLightingMode.None;
            if (meshRenderer.lightmapIndex >= 65534 || meshRenderer.lightmapIndex < 0)
                staticLightingMode = StaticLightingMode.LightProbes;
            else if (meshRenderer.lightmapIndex >= 0)
                staticLightingMode = StaticLightingMode.Lightmapped;

            dstEntityManager.AddComponentData(entity, new PerInstanceCullingTag());
            dstEntityManager.AddComponentData(entity, new RenderBounds { Value = localBounds });

            var material = materials[id];

            if(staticLightingMode == StaticLightingMode.Lightmapped && lightMaps.isValid)
            {
                conversionSystem.DeclareAssetDependency(meshRenderer.gameObject, material);

                var localFlags = LightMappingFlags.Lightmapped;
                if (lightMaps.hasDirections)
                    localFlags |= LightMappingFlags.Directional;
                if (lightMaps.hasShadowMask)
                    localFlags |= LightMappingFlags.ShadowMask;

                var key = new MaterialLookupKey
                {
                    BaseMaterial = materials[id],
                    lightmaps = lightMaps,
                    Flags = localFlags
                };

                var lookUp = createdMaterials ?? new Dictionary<MaterialLookupKey, Material>();
                if (lookUp.TryGetValue(key, out Material result))
                {
                    material = result;
                }
                else
                {
                    material = new Material(materials[id]);
                    material.name = $"{material.name}_Lightmapped_";
                    material.EnableKeyword("LIGHTMAP_ON");

                    material.SetTexture("unity_Lightmaps", lightMaps.colors);
                    material.SetTexture("unity_LightmapsInd", lightMaps.directions);
                    material.SetTexture("unity_ShadowMasks", lightMaps.shadowMasks);

                    if (lightMaps.hasDirections)
                    {
                        material.name = material.name + "_DIRLIGHTMAP";
                        material.EnableKeyword("DIRLIGHTMAP_COMBINED");
                    }

                    if (lightMaps.hasShadowMask)
                    {
                        material.name = material.name + "_SHADOW_MASK";
                    }

                    lookUp[key] = material;
                }
                dstEntityManager.AddComponentData(entity, new BuiltinMaterialPropertyUnity_LightmapST() {Value = meshRenderer.lightmapScaleOffset});
                dstEntityManager.AddComponentData(entity, new BuiltinMaterialPropertyUnity_LightmapIndex() {Value = lightmapIndex});
                dstEntityManager.AddSharedComponentData(entity, lightMaps);
            }
            else if (staticLightingMode == StaticLightingMode.LightProbes)
            {
                if (meshRenderer.lightProbeUsage == LightProbeUsage.CustomProvided)
                    dstEntityManager.AddComponent<CustomProbeTag>(entity);
                else if (meshRenderer.lightProbeUsage == LightProbeUsage.BlendProbes
                         && LightmapSettings.lightProbes != null
                         && LightmapSettings.lightProbes.count > 0)
                    dstEntityManager.AddComponent<BlendProbeTag>(entity);
                else
                    dstEntityManager.AddComponent<AmbientProbeTag>(entity);
            }
            renderMesh.material = material;

            dstEntityManager.AddSharedComponentData(entity, renderMesh);

            if (flipWinding)
                dstEntityManager.AddComponent(entity, ComponentType.ReadWrite<RenderMeshFlippedWindingTag>());

            conversionSystem.ConfigureEditorRenderData(entity, meshRenderer.gameObject, true);

#if ENABLE_HYBRID_RENDERER_V2
            dstEntityManager.AddComponent(entity, ComponentType.ReadOnly<WorldToLocal_Tag>());

#if HDRP_9_0_0_OR_NEWER
            // HDRP previous frame matrices (for motion vectors)
            if (needMotionVectorPass)
            {
                dstEntityManager.AddComponent(entity, ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_MatrixPreviousM>());
                dstEntityManager.AddComponent(entity, ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_MatrixPreviousMI_Tag>());
            }
            dstEntityManager.AddComponentData(entity, CreateMotionVectorsParams(ref renderMesh, ref meshRenderer));
#endif

            dstEntityManager.AddComponentData(entity, new BuiltinMaterialPropertyUnity_RenderingLayer
            {
                Value = new uint4(meshRenderer.renderingLayerMask, 0, 0, 0)
            });

            dstEntityManager.AddComponentData(entity, new BuiltinMaterialPropertyUnity_WorldTransformParams
            {
                Value = flipWinding ? new float4(0, 0, 0, -1) : new float4(0, 0, 0, 1)
            });

#if URP_9_0_0_OR_NEWER
            // Default initialized light data for URP
            dstEntityManager.AddComponentData(entity, new BuiltinMaterialPropertyUnity_LightData
            {
                Value = new float4(0, 0, 1, 0)
            });
#endif
#endif
        }

        public static void Convert(
            EntityManager dstEntityManager,
            GameObjectConversionSystem conversionSystem,
            bool attachToPrimaryEntityForSingleMaterial,
            Renderer meshRenderer,
            Mesh mesh,
            List<Material> materials,
            Dictionary<MaterialLookupKey, Material> createdMaterials,
            LightMaps lightMaps,
            int lightmapIndex,
            Transform root,
            AABB localBounds)
        {
            var materialCount = materials.Count;

            // Don't add RenderMesh (and other required components) unless both mesh and material are assigned.
            if (mesh == null || materialCount == 0)
            {
                Debug.LogWarning(
                    "MeshRenderer is not converted because either the assigned mesh is null or no materials are assigned.",
                    meshRenderer);
                return;
            }

            //@TODO: Transform system should handle RenderMeshFlippedWindingTag automatically. This should not be the responsibility of the conversion system.
            float4x4 localToWorld = root.localToWorldMatrix;
            var flipWinding = math.determinant(localToWorld) < 0.0;

            if (materialCount == 1 && attachToPrimaryEntityForSingleMaterial)
            {
                var meshEntity = conversionSystem.GetPrimaryEntity(meshRenderer);

                AddComponentsToEntity(
                    meshEntity,
                    dstEntityManager,
                    conversionSystem,
                    meshRenderer,
                    mesh,
                    materials,
                    createdMaterials,
                    flipWinding,
                    0,
                    lightMaps,
                    lightmapIndex,
                    localBounds);
            }
            else
            {
                var rootEntity = conversionSystem.GetPrimaryEntity(root);

                for (var m = 0; m != materialCount; m++)
                {
                    var meshEntity = conversionSystem.CreateAdditionalEntity(meshRenderer);

                    dstEntityManager.AddComponentData(meshEntity, new LocalToWorld {Value = localToWorld});
                    if (!dstEntityManager.HasComponent<Static>(meshEntity))
                    {
                        dstEntityManager.AddComponentData(meshEntity, new Parent {Value = rootEntity});
                        dstEntityManager.AddComponentData(meshEntity,
                            new LocalToParent {Value = float4x4.identity});
                    }

                    AddComponentsToEntity(
                        meshEntity,
                        dstEntityManager,
                        conversionSystem,
                        meshRenderer,
                        mesh,
                        materials,
                        createdMaterials,
                        flipWinding,
                        m,
                        lightMaps,
                        lightmapIndex,
                        localBounds);
                }
            }
        }
    }
}
