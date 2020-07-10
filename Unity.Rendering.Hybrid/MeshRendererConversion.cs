using System.Collections.Generic;
using Unity.Transforms;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Rendering
{
    [ConverterVersion("unity", 9)]
    [WorldSystemFilter(WorldSystemFilterFlags.HybridGameObjectConversion)]
    class MeshRendererConversion : GameObjectConversionSystem
    {
        const bool AttachToPrimaryEntityForSingleMaterial = true;

        protected override void OnUpdate()
        {
            var materials = new List<Material>(10);
            Entities.WithNone<TextMesh>().ForEach((MeshRenderer meshRenderer, MeshFilter meshFilter) =>
            {
                var mesh = meshFilter.sharedMesh;
                meshRenderer.GetSharedMaterials(materials);

                Convert(DstEntityManager, this, AttachToPrimaryEntityForSingleMaterial, meshRenderer, mesh, materials, meshRenderer.transform, mesh.bounds.ToAABB());
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

        private static void AddComponentsToEntity(
            Entity entity,
            EntityManager dstEntityManager,
            GameObjectConversionSystem conversionSystem,
            Renderer meshRenderer,
            Mesh mesh,
            Material material,
            bool flipWinding,
            int id,
            AABB localBounds)
        {
            var needMotionVectorPass = (meshRenderer.motionVectorGenerationMode == MotionVectorGenerationMode.Object) ||
                                       (meshRenderer.motionVectorGenerationMode == MotionVectorGenerationMode.ForceNoMotion);

            var renderMesh = new RenderMesh
            {
                mesh = mesh,
                material = material,
                castShadows = meshRenderer.shadowCastingMode,
                receiveShadows = meshRenderer.receiveShadows,
                layer = meshRenderer.gameObject.layer,
                subMesh = id,
                needMotionVectorPass = needMotionVectorPass
            };

            dstEntityManager.AddSharedComponentData(entity, renderMesh);

            dstEntityManager.AddComponentData(entity, new PerInstanceCullingTag());
            dstEntityManager.AddComponentData(entity, new RenderBounds { Value = localBounds });

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

            if (meshRenderer.lightProbeUsage == LightProbeUsage.CustomProvided)
            {
                dstEntityManager.AddComponent<CustomProbeTag>(entity);
            }
            else
            {
                dstEntityManager.AddComponent<AmbientProbeTag>(entity);
            }
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
            Transform root,
            AABB localBounds)
        {
            var materialCount = materials.Count;

            // Don't add RenderMesh (and other required components) unless both mesh and material are assigned.
            if (mesh == null || materialCount == 0)
            {
                Debug.LogWarning("MeshRenderer is not converted because either the assigned mesh is null or no materials are assigned.", meshRenderer);
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
                    materials[0],
                    flipWinding,
                    0,
                    localBounds);
            }
            else
            {
                var rootEntity = conversionSystem.GetPrimaryEntity(root);

                for (var m = 0; m != materialCount; m++)
                {
                    var meshEntity = conversionSystem.CreateAdditionalEntity(meshRenderer);

                    dstEntityManager.AddComponentData(meshEntity, new LocalToWorld { Value = localToWorld });
                    if (!dstEntityManager.HasComponent<Static>(meshEntity))
                    {
                        dstEntityManager.AddComponentData(meshEntity, new Parent { Value = rootEntity });
                        dstEntityManager.AddComponentData(meshEntity, new LocalToParent { Value = float4x4.identity });
                    }

                    AddComponentsToEntity(
                        meshEntity,
                        dstEntityManager,
                        conversionSystem,
                        meshRenderer,
                        mesh,
                        materials[m],
                        flipWinding,
                        m,
                        localBounds);
                }
            }
        }
    }
}
