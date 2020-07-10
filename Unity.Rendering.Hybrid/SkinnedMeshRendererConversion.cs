using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;
using Unity.Deformations;
using Unity.Mathematics;

namespace Unity.Rendering
{
    [ConverterVersion("unity", 4)]
    [WorldSystemFilter(WorldSystemFilterFlags.HybridGameObjectConversion)]
    class SkinnedMeshRendererConversion : GameObjectConversionSystem
    {
        const bool k_AttachToPrimaryEntityForSingleMaterial = false;

        protected override void OnUpdate()
        {
            var materials = new List<Material>(10);
            Entities.ForEach((SkinnedMeshRenderer meshRenderer) =>
            {
                meshRenderer.GetSharedMaterials(materials);
                var mesh = meshRenderer.sharedMesh;
                var root = meshRenderer.rootBone ? meshRenderer.rootBone : meshRenderer.transform;
                var hasSkinning = mesh == null ? false : mesh.boneWeights.Length > 0 && mesh.bindposes.Length > 0;
                var hasBlendShapes = mesh == null ? false : mesh.blendShapeCount > 0;
                var deformedEntity = GetPrimaryEntity(meshRenderer);

                // Convert Renderers as normal MeshRenderers.
                MeshRendererConversion.Convert(DstEntityManager, this, k_AttachToPrimaryEntityForSingleMaterial, meshRenderer, mesh, materials, root, meshRenderer.localBounds.ToAABB());

                foreach (var rendererEntity in GetEntities(meshRenderer))
                {
                    if (DstEntityManager.HasComponent<RenderMesh>(rendererEntity))
                    {
                        // Add relevant deformation tags to converted render entities and link them to the DeformedEntity.
#if ENABLE_COMPUTE_DEFORMATIONS
                        DstEntityManager.AddComponentData(rendererEntity, new DeformedMeshIndex());
#endif
                        DstEntityManager.AddComponentData(rendererEntity, new DeformedEntity { Value = deformedEntity });

                        if (hasSkinning)
                            DstEntityManager.AddComponent<SkinningTag>(rendererEntity);

                        if (hasBlendShapes)
                            DstEntityManager.AddComponent<BlendShapeTag>(rendererEntity);
                    }
                }

                // Fill the blend shape weights.
                if (hasBlendShapes && !DstEntityManager.HasComponent<BlendShapeWeight>(deformedEntity))
                {
                    DstEntityManager.AddBuffer<BlendShapeWeight>(deformedEntity);
                    var weights = DstEntityManager.GetBuffer<BlendShapeWeight>(deformedEntity);
                    weights.ResizeUninitialized(meshRenderer.sharedMesh.blendShapeCount);

                    for (int i = 0; i < weights.Length; ++i)
                    {
                        weights[i] = new BlendShapeWeight { Value = meshRenderer.GetBlendShapeWeight(i) };
                    }
                }

                // Fill the skin matrices with bindpose skin matrices.
                if (hasSkinning && !DstEntityManager.HasComponent<SkinMatrix>(deformedEntity))
                {
                    var bones = meshRenderer.bones;
                    var rootMatrixInv = root.localToWorldMatrix.inverse;

                    DstEntityManager.AddBuffer<SkinMatrix>(deformedEntity);
                    var skinMatrices = DstEntityManager.GetBuffer<SkinMatrix>(deformedEntity);
                    skinMatrices.ResizeUninitialized(bones.Length);

                    for (int i = 0; i < bones.Length; ++i)
                    {
                        var bindPose = meshRenderer.sharedMesh.bindposes[i];
                        var boneMatRootSpace = math.mul(rootMatrixInv, bones[i].localToWorldMatrix);
                        var skinMatRootSpace = math.mul(boneMatRootSpace, bindPose);
                        skinMatrices[i] = new SkinMatrix { Value = new float3x4(skinMatRootSpace.c0.xyz, skinMatRootSpace.c1.xyz, skinMatRootSpace.c2.xyz, skinMatRootSpace.c3.xyz) };
                    }
                }
            });
        }
    }
}
