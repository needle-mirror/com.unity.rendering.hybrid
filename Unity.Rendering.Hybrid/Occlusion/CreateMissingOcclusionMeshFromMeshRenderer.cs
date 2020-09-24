#if ENABLE_UNITY_OCCLUSION && ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_2_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine.Rendering;
using Unity.Transforms;

namespace Unity.Rendering.Occlusion
{
    [ConverterVersion("vlad-andreev", 1)]
    [UpdateInGroup(typeof(GameObjectDeclareReferencedObjectsGroup))]
    class MeshRendererDeclareAssets : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((Occluder occluder) =>
            {
                if (!occluder.gameObject.activeInHierarchy)
                    return;

                var sharedMesh = occluder.Mesh;
                if (sharedMesh == null)
                {
                    Debug.Log($"sharedMesh is null on {occluder.name}!");
                }
                DeclareReferencedAsset(sharedMesh);
            });
        }
    }

    [ConverterVersion("vlad-andreev", 1)]
    [WorldSystemFilter(WorldSystemFilterFlags.HybridGameObjectConversion)]
    [UpdateAfter(typeof(MeshRendererConversion))]
    class MeshCreateOcclusionData : GameObjectConversionSystem
    {
        unsafe protected override void OnUpdate()
        {
            Entities.WithNone<TextMesh>().ForEach((Occluder occluder) =>
            {
                if (!occluder.gameObject.activeInHierarchy || occluder.Mesh == null)
                    return;

                var mesh = occluder.Mesh;
                var entity = GetPrimaryEntity(mesh);

                if (mesh.subMeshCount > 1)
                {
                    for (int i = 0; i < mesh.subMeshCount; i++)
                    {
                        var subMesh = mesh.GetSubMesh(i);
                        entity = CreateAdditionalEntity(mesh);
                        AddComponents(entity, mesh, i);
                    }
                }
                else
                {
                    AddComponents(entity, mesh, 0);
                }

            });
        }

        unsafe protected void AddComponents(Entity entity, Mesh mesh, int submeshIndex)
        {
            var subMesh = mesh.GetSubMesh(submeshIndex);
            var verts = mesh.vertices;
            var occlusionMeshAsset = new OcclusionMeshAsset();

            var vertices = new NativeArray<float4>(subMesh.vertexCount, Allocator.Temp);
            for (int i = 0; i < subMesh.vertexCount; ++i)
            {
                vertices[i] = new float4(verts[i + subMesh.firstVertex], 1.0f);
            }

            occlusionMeshAsset.vertexCount = subMesh.vertexCount;
            occlusionMeshAsset.vertexData = BlobAssetReference<float4>.Create(
                vertices.GetUnsafeReadOnlyPtr(), sizeof(float4) * vertices.Length);

            vertices.Dispose();

            var indices = new NativeArray<int>(subMesh.indexCount, Allocator.Temp);

            fixed (int* srcIndices = mesh.triangles)
            {
                for (int i = 0; i < indices.Length; i++)
                {
                    indices[i] = srcIndices[subMesh.indexStart + i] - subMesh.firstVertex;
                    if (indices[i] < 0 || indices[i] > subMesh.vertexCount)
                    {
                        Debug.Log($"broken index at {i}: {indices[i]}");
                    }
                }
            }

            occlusionMeshAsset.indexCount = indices.Length;
            occlusionMeshAsset.indexData = BlobAssetReference<int>.Create(
                indices.GetUnsafeReadOnlyPtr(), sizeof(int) * indices.Length);

            indices.Dispose();

            DstEntityManager.AddComponentData(entity, occlusionMeshAsset);
        }
    }

    [UpdateAfter(typeof(MeshCreateOcclusionData))]
    [ConverterVersion("vlad-andreev", 2)]
    class MeshAttachOcclusionData : GameObjectConversionSystem
    {
        unsafe protected override void OnUpdate()
        {
            // copy the shared component over to the renderable entities, and add the job-safe OcclusionMesh
            // component to the corresponding chunks
            Entities.WithNone<TextMesh>().ForEach((Occluder occluder) =>
            {
                var mesh = occluder.Mesh;

                if (!occluder.gameObject.activeInHierarchy || occluder.Mesh == null)
                    return;

                var meshRenderer = occluder.gameObject.GetComponent<MeshRenderer>();

                var entity = GetPrimaryEntity(occluder);// meshRenderer);

                var meshEntity = GetPrimaryEntity(mesh);

                if (meshRenderer == null)
                {
                    var occlusionMeshAsset = DstEntityManager.GetComponentData<OcclusionMeshAsset>(meshEntity);
                    DstEntityManager.AddComponentData(entity, new OcclusionMesh(ref occlusionMeshAsset, occluder));

                    var localToWorld = occluder.gameObject.transform.localToWorldMatrix;
                    DstEntityManager.AddComponentData(entity, new LocalToWorld { Value = localToWorld });
                }
                else
                {
                    var entities = GetEntities(meshRenderer).ToArray();

                    int entityCount = entities.Count();
                    if (meshRenderer.sharedMaterials.Length <= 1)
                    {
                        var occlusionMeshAsset = DstEntityManager.GetComponentData<OcclusionMeshAsset>(meshEntity);
                        DstEntityManager.AddComponentData<OcclusionMesh>(entity, new OcclusionMesh(ref occlusionMeshAsset, occluder));
                    }
                    else
                    {
                        var meshEntities = GetEntities(mesh).ToArray();
                        var meshEntityCount = meshEntities.Length;

                        int lastOcclusionAsset = -1;
                        for (int i = 0; i < entityCount; i++)
                        {
                            bool found = false;
                            if (DstEntityManager.HasComponent<RenderMesh>(entities[i]))
                            {
                                var renderMesh = DstEntityManager.GetSharedComponentData<RenderMesh>(entities[i]);
                                for (int k = 0; k < meshEntityCount; k++)
                                {
                                    if (DstEntityManager.HasComponent<OcclusionMeshAsset>(meshEntities[k]))
                                    {
                                        lastOcclusionAsset = k;
                                        if (renderMesh.subMesh == k-1)
                                        {
                                            var occlusionMeshAsset = DstEntityManager.GetComponentData<OcclusionMeshAsset>(meshEntities[k]);
                                            DstEntityManager.AddComponentData<OcclusionMesh>(entities[i], new OcclusionMesh(ref occlusionMeshAsset, occluder));
                                            found = true;
                                            break;
                                        }
                                    }
                                }

                                if (!found)
                                {
                                    if (lastOcclusionAsset >= 0)
                                    {
									    // if we can't match occlusion assets to submeshes, that probably means that the
										// occlusion mesh is not the same as the render geometry.  in this case we assume
										// (for now at least) that the occlusion mesh has a single submesh.
										//
										// TODO:  more clear heuristics + verification
                                        var occlusionMeshAsset = DstEntityManager.GetComponentData<OcclusionMeshAsset>(meshEntities[lastOcclusionAsset]);
                                        DstEntityManager.AddComponentData<OcclusionMesh>(entities[i], new OcclusionMesh(ref occlusionMeshAsset, occluder));
                                    }
                                    else
                                    {
                                        Debug.LogWarning($"Couldn't match an occlusion asset to a submesh for {meshRenderer.name}");
                                    }
                                }
                            }
                        }
                    }
                }
            });

            Entities.ForEach((UnityEngine.MeshRenderer meshRenderer, UnityEngine.MeshFilter meshFilter, Occludee occludee) =>
            {
                if (occludee.enabled)
                {
                    var entities = GetEntities(meshRenderer);
                    foreach (var entity in entities)
                        DstEntityManager.AddComponentData(entity, new OcclusionTest(true));
                }
            });
        }
    }
}

#endif
