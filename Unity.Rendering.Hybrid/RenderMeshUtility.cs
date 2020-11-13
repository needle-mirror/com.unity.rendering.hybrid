// #define DISABLE_HYBRID_TRANSPARENCY_BATCH_PARTITIONING
// #define USE_HYBRID_SHARED_COMPONENT_OVERRIDES

#if HDRP_9_0_0_OR_NEWER
#define USE_HYBRID_MOTION_PASS
#endif

#if URP_9_0_0_OR_NEWER
#define USE_HYBRID_BUILTIN_LIGHTDATA
#endif

using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Rendering
{
    /// <summary>
    /// Describes how to setup and configure Hybrid Rendered entities. Can be used to convert GameObjects
    /// into entities, or to set component values on entities directly.
    /// </summary>
    public struct RenderMeshDescription
    {
        /// <summary>
        /// The <see cref="Rendering.RenderMesh"/> that will be used by the Hybrid Renderer to render this entity.
        /// To easily create a <see cref="Rendering.RenderMesh"/> from higher level objects, you can use
        /// <see cref="RenderMeshUtility.CreateRenderMesh"/>.
        /// </summary>
        public RenderMesh RenderMesh;

        /// <summary>
        /// What kinds of motion vectors are to be generated for this entity, if any.
        /// Corresponds to <see cref="Renderer.motionVectorGenerationMode"/>.
        ///
        /// Only affects rendering pipelines that use motion vectors.
        /// </summary>
        public MotionVectorGenerationMode MotionMode;

        /// <summary>
        /// Which rendering layer the entity lives on.
        /// Corresponds to <see cref="Renderer.renderingLayerMask"/>.
        /// </summary>
        public uint RenderingLayerMask;

        /// <summary>
        /// If this is set to true, the triangle winding will be flipped when rendering.
        /// </summary>
        public bool FlipWinding;

        /// <summary>
        /// Construct a <see cref="RenderMeshDescription"/> using defaults from the given
        /// <see cref="Renderer"/> and <see cref="Mesh"/> objects.
        /// </summary>
        /// <param name="renderer">The renderer object (e.g. a <see cref="MeshRenderer"/>) to get default settings from.</param>
        /// <param name="mesh">The mesh to use and get default bounds from.</param>
        /// <param name="sharedMaterials">The list of materials to render the entity with.
        /// If the list is null or empty, <see cref="Renderer.GetSharedMaterials"/> will be used to obtain the list.
        /// An explicit list can be supplied if you have already called <see cref="Renderer.GetSharedMaterials"/> previously,
        /// or if you want to use different materials.</param>
        /// <param name="subMeshIndex">The sub-mesh of the mesh to use for rendering. The corresponding material index
        /// from <see cref="sharedMaterials"/> will be used as the material for that sub-mesh.</param>
        public RenderMeshDescription(
            Renderer renderer,
            Mesh mesh,
            List<Material> sharedMaterials = null,
            int subMeshIndex = 0)
        {
            Debug.Assert(renderer != null, "Must have a non-null Renderer to create RenderMeshDescription.");
            Debug.Assert(mesh != null, "Must have a non-null Mesh to create RenderMeshDescription.");

            if (sharedMaterials is null)
                sharedMaterials = new List<Material>(capacity: 10);

            if (sharedMaterials.Count == 0)
                renderer.GetSharedMaterials(sharedMaterials);

            Debug.Assert(subMeshIndex >= 0 && subMeshIndex < sharedMaterials.Count,
                "Sub-mesh index out of bounds, no matching material.");

            var motionVectorGenerationMode = renderer.motionVectorGenerationMode;
            var needMotionVectorPass =
                (motionVectorGenerationMode == MotionVectorGenerationMode.Object) ||
                (motionVectorGenerationMode == MotionVectorGenerationMode.ForceNoMotion);

            RenderMesh = new RenderMesh
            {
                mesh = mesh,
                material = sharedMaterials[subMeshIndex],
                subMesh = subMeshIndex,
                layer = renderer.gameObject.layer,
                castShadows = renderer.shadowCastingMode,
                receiveShadows = renderer.receiveShadows,
                needMotionVectorPass = needMotionVectorPass,
            };

            RenderingLayerMask = renderer.renderingLayerMask;
            FlipWinding = false;
            MotionMode = motionVectorGenerationMode;
        }

        /// <summary>
        /// Construct a <see cref="RenderMeshDescription"/> using the given values.
        /// </summary>
        public RenderMeshDescription(
            Mesh mesh,
            Material material,
            ShadowCastingMode shadowCastingMode = ShadowCastingMode.Off,
            bool receiveShadows = false,
            MotionVectorGenerationMode motionVectorGenerationMode = MotionVectorGenerationMode.Camera,
            int layer = 0,
            int subMeshIndex = 0,
            uint renderingLayerMask = 1)
        {
            Debug.Assert(material != null, "Must have a non-null Material to create RenderMeshDescription.");
            Debug.Assert(mesh != null, "Must have a non-null Mesh to create RenderMeshDescription.");

            var needMotionVectorPass =
                (motionVectorGenerationMode == MotionVectorGenerationMode.Object) ||
                (motionVectorGenerationMode == MotionVectorGenerationMode.ForceNoMotion);

            RenderMesh = new RenderMesh
            {
                mesh = mesh,
                material = material,
                subMesh = subMeshIndex,
                layer = layer,
                castShadows = shadowCastingMode,
                receiveShadows = receiveShadows,
                needMotionVectorPass = needMotionVectorPass,
            };

            RenderingLayerMask = renderingLayerMask;
            FlipWinding = false;
            MotionMode = motionVectorGenerationMode;
        }

        /// <summary>
        /// Returns true if the entity needs to be drawn in any per-object motion pass.
        /// </summary>
        public bool IsInMotionPass =>
            MotionMode != MotionVectorGenerationMode.Camera;

        /// <summary>
        /// Returns true if the <see cref="RenderMeshDescription"/> is valid for rendering.
        /// Returns false and logs debug warnings if invalid settings are detected.
        /// </summary>
        /// <returns>True if the object is valid for rendering.</returns>
        public bool IsValid()
        {
            bool valid = true;

            if (RenderMesh.mesh == null)
            {
                Debug.LogWarning("RenderMesh must have a valid non-null Mesh.");
                valid = false;
            }
            else if (RenderMesh.subMesh < 0 || RenderMesh.subMesh >= RenderMesh.mesh.subMeshCount)
            {
                Debug.LogWarning("RenderMesh subMesh index out of bounds.");
                valid = false;
            }

            if (RenderMesh.material == null)
            {
                Debug.LogWarning("RenderMesh must have a valid non-null Material.");
                valid = false;
            }

            return valid;
        }
    }

    /// <summary>
    /// Helper class that contains static methods for populating entities
    /// so that they are compatible with the Hybrid Renderer.
    /// </summary>
    public static class RenderMeshUtility
    {
        static float4 CreateMotionVectorsParams(MotionVectorGenerationMode motionVectorGenerationMode)
        {
            float s_bias = -0.001f;
            // TODO: Double buffered positions are not implemented yet
            // float hasLastPositionStream = (mesh.needMotionVectorPass && deformedMotionVectorsMode == DeformedMotionVectorsMode.DeformedMotionVectors)
            //     ? 1.0f
            //     : 0.0f;
            float hasLastPositionStream = 0;
            float forceNoMotion = (motionVectorGenerationMode == MotionVectorGenerationMode.ForceNoMotion) ? 0.0f : 1.0f;
            float cameraVelocity = (motionVectorGenerationMode == MotionVectorGenerationMode.Camera) ? 0.0f : 1.0f;
            return new float4(hasLastPositionStream, forceNoMotion, s_bias, cameraVelocity);
        }

#if ENABLE_HYBRID_RENDERER_V2
        private static ComponentTypes kHybridComponentsNoMotion = new ComponentTypes(new ComponentType[]
        {
            // Absolute minimum set of components required by Hybrid Renderer
            // to be considered for rendering. Entities without these components will
            // not match queries and will never be rendered.
            ComponentType.ReadWrite<WorldRenderBounds>(),
            ComponentType.ReadWrite<LocalToWorld>(),
            ComponentType.ReadWrite<RenderMesh>(),
            ComponentType.ChunkComponent<ChunkWorldRenderBounds>(),
            ComponentType.ChunkComponent<HybridChunkInfo>(),
            // Extra transform related components required to render correctly
            // using many default SRP shaders. Custom shaders could potentially
            // work without it.
            ComponentType.ReadWrite<WorldToLocal_Tag>(),
            // Components required by Hybrid Renderer visibility culling.
            ComponentType.ReadWrite<RenderBounds>(),
            ComponentType.ReadWrite<PerInstanceCullingTag>(),
            // Components for setting common built-in material properties required
            // by most SRP shaders that don't fall into the other categories.
#if USE_HYBRID_SHARED_COMPONENT_OVERRIDES
            ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_RenderingLayer_Shared>(),
            ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_WorldTransformParams_Shared>(),
#else
            ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_RenderingLayer>(),
            ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_WorldTransformParams>(),
#endif
            // Components required by objects that use per-object light lists. Currently only
            // used by URP, and there is no automatic support in Hybrid Renderer.
            // Can be empty if disabled.
#if USE_HYBRID_BUILTIN_LIGHTDATA
            ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_LightData>(),
#endif
        });

        private static ComponentTypes kHybridComponentsWithMotion = new ComponentTypes(new ComponentType[]
        {
            // Absolute minimum set of components required by Hybrid Renderer
            // to be considered for rendering. Entities without these components will
            // not match queries and will never be rendered.
            ComponentType.ReadWrite<WorldRenderBounds>(),
            ComponentType.ReadWrite<LocalToWorld>(),
            ComponentType.ReadWrite<RenderMesh>(),
            ComponentType.ChunkComponent<ChunkWorldRenderBounds>(),
            ComponentType.ChunkComponent<HybridChunkInfo>(),
            // Extra transform related components required to render correctly
            // using many default SRP shaders. Custom shaders could potentially
            // work without it.
            ComponentType.ReadWrite<WorldToLocal_Tag>(),
            // Components required by Hybrid Renderer visibility culling.
            ComponentType.ReadWrite<RenderBounds>(),
            ComponentType.ReadWrite<PerInstanceCullingTag>(),
            // Components for setting common built-in material properties required
            // by most SRP shaders that don't fall into the other categories.
#if USE_HYBRID_SHARED_COMPONENT_OVERRIDES
            ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_RenderingLayer_Shared>(),
            ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_WorldTransformParams_Shared>(),
#else
            ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_RenderingLayer>(),
            ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_WorldTransformParams>(),
#endif
            // Components required by objects that use per-object light lists. Currently only
            // used by URP, and there is no automatic support in Hybrid Renderer.
            // Can be empty if disabled.
#if USE_HYBRID_BUILTIN_LIGHTDATA
            ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_LightData>(),
#endif
            // Components required by objects that need to be rendered in per-object motion passes.
#if USE_HYBRID_MOTION_PASS
            ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_MatrixPreviousM>(),
#if USE_HYBRID_SHARED_COMPONENT_OVERRIDES
            ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_MotionVectorsParams_Shared>(),
#else
            ComponentType.ReadWrite<BuiltinMaterialPropertyUnity_MotionVectorsParams>(),
#endif
#endif
        });

#else
        private static ComponentTypes kHybridV1Components = new ComponentTypes(new ComponentType[]
        {
            ComponentType.ReadWrite<WorldRenderBounds>(),
            ComponentType.ReadWrite<LocalToWorld>(),
            ComponentType.ReadWrite<RenderMesh>(),
            ComponentType.ChunkComponent<ChunkWorldRenderBounds>(),
            ComponentType.ReadWrite<RenderBounds>(),
            ComponentType.ReadWrite<PerInstanceCullingTag>(),
        });
#endif

        // Use a boolean constant for guarding most of the code so both ifdef branches are
        // always compiled.
        // This leads to the following warning due to the other branch being unreachable, so disable it
        // warning CS0162: Unreachable code detected
#pragma warning disable CS0162

#if USE_HYBRID_SHARED_COMPONENT_OVERRIDES
        private const bool kUseSharedComponentOverrides = true;
#else
        private const bool kUseSharedComponentOverrides = false;
#endif
#if USE_HYBRID_MOTION_PASS
        private const bool kUseHybridMotionPass = true;
#else
        private const bool kUseHybridMotionPass = false;
#endif
#if USE_HYBRID_BUILTIN_LIGHTDATA
        private const bool kUseHybridBuiltinLightData = true;
#else
        private const bool kUseHybridBuiltinLightData = false;
#endif
        /// <summary>
        /// Set the Hybrid Renderer component values to render the given entity using the given description.
        /// Any missing components will be added, which results in structural changes.
        /// </summary>
        /// <param name="entity">The entity to set the component values for.</param>
        /// <param name="entityManager">The <see cref="EntityManager"/> used to set the component values.</param>
        /// <param name="renderMeshDescription">The description that determines how the entity is to be rendered.</param>
        /// <example><code>
        /// void CodeExample()
        /// {
        ///     var world = World.DefaultGameObjectInjectionWorld;
        ///     var entityManager = world.EntityManager;
        ///
        ///     var desc = new RenderMeshDescription(
        ///         Mesh,
        ///         Material);
        ///
        ///     var renderMesh = RenderMeshUtility.CreateRenderMesh(Mesh, Material);
        ///     // RenderMeshUtility can be used to easily create Hybrid Renderer
        ///     // compatible entities, but it can only be called from the main thread.
        ///     var entity = entityManager.CreateEntity();
        ///     RenderMeshUtility.AddComponents(
        ///         entity,
        ///         entityManager,
        ///         desc);
        ///     entityManager.AddComponentData(entity, new ExampleComponent());
        ///
        ///     // If multiple similar entities are to be created, 'entity' can now
        ///     // be instantiated using Instantiate(), and its component values changed
        ///     // afterwards.
        ///     // This can also be done in Burst jobs using EntityCommandBuffer.ParallelWriter.
        ///     var secondEntity = entityManager.Instantiate(entity);
        ///     entityManager.SetComponentData(secondEntity, new Translation {Value = new float3(1, 2, 3)});
        /// }
        /// </code></example>
        /// <seealso cref="AddComponents(Unity.Entities.Entity,Unity.Entities.EntityCommandBuffer,Unity.Rendering.RenderMeshDescription)"/>
        public static void AddComponents(
            Entity entity,
            EntityManager entityManager,
            in RenderMeshDescription renderMeshDescription)
        {
#if UNITY_EDITOR
            // Skip the validation check in the player to minimize overhead.
            if (!renderMeshDescription.IsValid())
                return;
#endif

            // Add all components up front using as few calls as possible.
#if ENABLE_HYBRID_RENDERER_V2
            if (renderMeshDescription.IsInMotionPass && kUseHybridMotionPass)
                entityManager.AddComponents(entity, kHybridComponentsWithMotion);
            else
                entityManager.AddComponents(entity, kHybridComponentsNoMotion);
#else
            entityManager.AddComponents(entity, kHybridV1Components);
#endif

            if (renderMeshDescription.FlipWinding)
                entityManager.AddComponent(entity, ComponentType.ReadWrite<RenderMeshFlippedWindingTag>());

            var renderMesh = renderMeshDescription.RenderMesh;
            entityManager.SetSharedComponentData(entity, renderMesh);

            var localBounds = renderMesh.mesh.bounds.ToAABB();
            entityManager.SetComponentData(entity, new RenderBounds { Value = localBounds });

#if ENABLE_HYBRID_RENDERER_V2
            // HDRP previous frame matrices (for motion vectors)
            if (renderMeshDescription.IsInMotionPass && kUseHybridMotionPass)
            {
                if (kUseSharedComponentOverrides)
                {
                    entityManager.SetSharedComponentData(entity,
                        new BuiltinMaterialPropertyUnity_MotionVectorsParams_Shared
                        {
                            Value = CreateMotionVectorsParams(renderMeshDescription.MotionMode)
                        });
                }
                else
                {
                    entityManager.SetComponentData(entity,
                        new BuiltinMaterialPropertyUnity_MotionVectorsParams
                        {
                            Value = CreateMotionVectorsParams(renderMeshDescription.MotionMode)
                        });
                }
            }

            if (kUseSharedComponentOverrides)
            {
                entityManager.SetSharedComponentData(entity, new BuiltinMaterialPropertyUnity_RenderingLayer_Shared
                {
                    Value = new uint4(renderMeshDescription.RenderingLayerMask, 0, 0, 0)
                });

                entityManager.SetSharedComponentData(entity,
                    new BuiltinMaterialPropertyUnity_WorldTransformParams_Shared
                    {
                        Value = renderMeshDescription.FlipWinding
                            ? new float4(0, 0, 0, -1)
                            : new float4(0, 0, 0, 1)
                    });
            }
            else
            {
                entityManager.SetComponentData(entity, new BuiltinMaterialPropertyUnity_RenderingLayer
                {
                    Value = new uint4(renderMeshDescription.RenderingLayerMask, 0, 0, 0)
                });

                entityManager.SetComponentData(entity, new BuiltinMaterialPropertyUnity_WorldTransformParams
                {
                    Value = renderMeshDescription.FlipWinding
                        ? new float4(0, 0, 0, -1)
                        : new float4(0, 0, 0, 1)
                });
            }

            if (kUseHybridBuiltinLightData)
            {
                // Default initialized light data for URP
                entityManager.SetComponentData(entity, new BuiltinMaterialPropertyUnity_LightData
                {
                    Value = new float4(0, 0, 1, 0)
                });
            }

#if !DISABLE_HYBRID_TRANSPARENCY_BATCH_PARTITIONING
            PartitionTransparentObjects(entity, entityManager, renderMeshDescription.RenderMesh);
#endif
#endif
        }

        /// <summary>
        /// Set the Hybrid Renderer component values to render the given entity using the given description.
        /// Any missing components will be added, which results in structural changes.
        /// </summary>
        /// <param name="entity">The entity to set the component values for.</param>
        /// <param name="ecb">The <see cref="EntityCommandBuffer"/> used to set the component values.</param>
        /// <param name="renderMeshDescription">The description that determines how the entity is to be rendered.</param>
        /// <example><code>
        /// void CodeExample()
        /// {
        ///     var world = World.DefaultGameObjectInjectionWorld;
        ///     var entityManager = world.EntityManager;
        ///
        ///     var desc = new RenderMeshDescription(
        ///         Mesh,
        ///         Material);
        ///
        ///     var renderMesh = RenderMeshUtility.CreateRenderMesh(Mesh, Material);
        ///     // RenderMeshUtility can be used to easily create Hybrid Renderer
        ///     // compatible entities, but it can only be called from the main thread.
        ///     var entity = entityManager.CreateEntity();
        ///     RenderMeshUtility.AddComponents(
        ///         entity,
        ///         entityManager,
        ///         desc);
        ///     entityManager.AddComponentData(entity, new ExampleComponent());
        ///
        ///     // If multiple similar entities are to be created, 'entity' can now
        ///     // be instantiated using Instantiate(), and its component values changed
        ///     // afterwards.
        ///     // This can also be done in Burst jobs using EntityCommandBuffer.ParallelWriter.
        ///     var secondEntity = entityManager.Instantiate(entity);
        ///     entityManager.SetComponentData(secondEntity, new Translation {Value = new float3(1, 2, 3)});
        /// }
        /// </code></example>
        /// <seealso cref="AddComponents(Unity.Entities.Entity,Unity.Entities.EntityManager,Unity.Rendering.RenderMeshDescription)"/>
        public static void AddComponents(
            Entity entity,
            EntityCommandBuffer ecb,
            in RenderMeshDescription renderMeshDescription)
        {
#if UNITY_EDITOR
            // Skip the validation check in the player to minimize overhead.
            if (!renderMeshDescription.IsValid())
                return;
#endif

            // NOTE: Keep this in sync with the AddHybridComponentsToEntity EntityManager version
            // Add all components up front using as few calls as possible.
#if ENABLE_HYBRID_RENDERER_V2
            if (renderMeshDescription.IsInMotionPass && kUseHybridMotionPass)
                ecb.AddComponent(entity, kHybridComponentsWithMotion);
            else
                ecb.AddComponent(entity, kHybridComponentsNoMotion);
#else
            ecb.AddComponent(entity, kHybridV1Components);
#endif

            if (renderMeshDescription.FlipWinding)
                ecb.AddComponent(entity, ComponentType.ReadWrite<RenderMeshFlippedWindingTag>());

            var renderMesh = renderMeshDescription.RenderMesh;
            ecb.SetSharedComponent(entity, renderMesh);

            var localBounds = renderMesh.mesh.bounds.ToAABB();
            ecb.SetComponent(entity, new RenderBounds { Value = localBounds });

#if ENABLE_HYBRID_RENDERER_V2
            // HDRP previous frame matrices (for motion vectors)
            if (renderMeshDescription.IsInMotionPass && kUseHybridMotionPass)
            {
                if (kUseSharedComponentOverrides)
                {
                    ecb.SetSharedComponent(entity,
                        new BuiltinMaterialPropertyUnity_MotionVectorsParams_Shared
                        {
                            Value = CreateMotionVectorsParams(renderMeshDescription.MotionMode)
                        });
                }
                else
                {
                    ecb.SetComponent(entity,
                        new BuiltinMaterialPropertyUnity_MotionVectorsParams
                        {
                            Value = CreateMotionVectorsParams(renderMeshDescription.MotionMode)
                        });
                }
            }

            if (kUseSharedComponentOverrides)
            {
                ecb.SetSharedComponent(entity, new BuiltinMaterialPropertyUnity_RenderingLayer_Shared
                {
                    Value = new uint4(renderMeshDescription.RenderingLayerMask, 0, 0, 0)
                });

                ecb.SetSharedComponent(entity,
                    new BuiltinMaterialPropertyUnity_WorldTransformParams_Shared
                    {
                        Value = renderMeshDescription.FlipWinding
                            ? new float4(0, 0, 0, -1)
                            : new float4(0, 0, 0, 1)
                    });
            }
            else
            {
                ecb.SetComponent(entity, new BuiltinMaterialPropertyUnity_RenderingLayer
                {
                    Value = new uint4(renderMeshDescription.RenderingLayerMask, 0, 0, 0)
                });

                ecb.SetComponent(entity, new BuiltinMaterialPropertyUnity_WorldTransformParams
                {
                    Value = renderMeshDescription.FlipWinding
                        ? new float4(0, 0, 0, -1)
                        : new float4(0, 0, 0, 1)
                });
            }

            if (kUseHybridBuiltinLightData)
            {
                // Default initialized light data for URP
                ecb.SetComponent(entity, new BuiltinMaterialPropertyUnity_LightData
                {
                    Value = new float4(0, 0, 1, 0)
                });
            }
#endif
        }
#pragma warning restore CS0162

        private static void PartitionTransparentObjects(
            Entity entity,
            EntityManager entityManager,
            in RenderMesh renderMesh)
        {
            if (IsMaterialTransparent(renderMesh.material))
            {
                int entityId = entity.Index;
                var hash = new xxHash3.StreamingState(true);

                hash.Update(entityId);
                if (entityManager.HasComponent<SceneSection>(entity))
                {
                    var sceneSection = entityManager.GetSharedComponentData<SceneSection>(entity);
                    hash.Update(sceneSection.SceneGUID);
                    hash.Update(sceneSection.Section);
                }

                uint2 transparentPartitionValue = hash.DigestHash64();
                entityManager.AddSharedComponentData(entity, new HybridBatchPartition
                {
                    PartitionValue = (ulong) transparentPartitionValue.x |
                                     ((ulong) transparentPartitionValue.y << 32),
                });
            }
        }

        /// <summary>
        /// Return true if the given <see cref="Material"/> is known to be transparent. Works
        /// for materials that use HDRP or URP conventions for transparent materials.
        /// </summary>
        private const string kSurfaceTypeHDRP = "_SurfaceType";
        private const string kSurfaceTypeURP = "_Surface";
        private static int kSurfaceTypeHDRPNameID = Shader.PropertyToID(kSurfaceTypeHDRP);
        private static int kSurfaceTypeURPNameID = Shader.PropertyToID(kSurfaceTypeURP);
        private static bool IsMaterialTransparent(Material material)
        {
            if (material == null)
                return false;

#if HDRP_9_0_0_OR_NEWER
            // Material.GetSurfaceType() is not public, so we try to do what it does internally.
            const int kSurfaceTypeTransparent = 1; // Corresponds to non-public SurfaceType.Transparent
            if (material.HasProperty(kSurfaceTypeHDRPNameID))
                return (int) material.GetFloat(kSurfaceTypeHDRPNameID) == kSurfaceTypeTransparent;
            else
                return false;
#elif URP_9_0_0_OR_NEWER
            const int kSurfaceTypeTransparent = 1; // Corresponds to SurfaceType.Transparent
            if (material.HasProperty(kSurfaceTypeURPNameID))
                return (int) material.GetFloat(kSurfaceTypeURPNameID) == kSurfaceTypeTransparent;
            else
                return false;
#else
            return false;
#endif
        }
    }
}
