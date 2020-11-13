# Change log

## [0.11.0] - 2020-11-13

### Added

* Frame queuing limiting solution to avoid hazards in the GPU uploader
* Hybrid V2 should now render objects with errors (e.g. missing or broken material) as bright magenta when the used SRP contains compatible error shaders, and display warnings.
* Support for lightmaps in hybrid renderer. You will need to bake with subscenes open, upon closing the lightmaps will be converted into the subscene. (Note: Requires release 10.1.0 of graphics packages).
* Support for lightprobes in hybrid renderer. Entities can dynamically look up the the current ambient probe or probe grid. (Note: Requires release 10.1.0 of graphics packages).
* Added error message when total used GPU memory is bigger than some backends can handle (1 GiB)
* HybridBatchPartition shared component that can force entities into separate batches.
* It is now possible to override DOTS instanced material properties using `ISharedComponentData`.
* RenderMeshDescription and RenderMeshUtility.AddComponent APIs to efficiently create Hybrid Rendered entities.

### Changed

* Log warning instead of error message when shader on SMR does not support DOTS Skinning
* Update minimum editor version to 2020.1.2f1

### Fixed

* Fixed float2 and float3 material properties like HDRP emissive color to work correctly.
* GPU buffer now grows by doubling, so initial startup lag is reduced.
* GPU resources are now cleaned up better in case of internal exceptions, leading to less errors in subsequent frames.
* Hybrid Renderer forces entities using URP and HDRP transparent materials into separate batches, so they are rendered in the correct order and produce correct rendering results.
* Fixed a bug with motion vector parameters not getting set correctly.
* HLOD conversion code now properly handles uninitialized components
* Removed internal frame queuing and replace it with frame fencing. Hybrid renderer will now longer wait for GPU buffers to be available, making it easier to see if you are GPU or CPU bound and avoiding some potential deadlocks.
* Disable deformation systems when no graphics device is present instead of throwing error.
* Fixed a bug with converting ambient light probe settings from GameObjects.

## [0.10.0] - 2020-09-24

### Added

* Error message when trying to convert SkinnedMeshRenderer that is using a shader that does not support skinning.

### Removed

* HybridRendererSettings asset was removed since memory management for the hybrid renderer data buffer is now automatic.

### Fixed

* Fixed missing mesh breaking subscene conversion
* Fixed chunk render bounds getting stale when RenderMesh shared component is changed.
* Improved Hybrid V2 memory usage during GPU uploading.
* Chunk render bounds getting stale when RenderMesh shared component is changed.
* Reduced Hybrid V2 peak memory use when batches are deleted and created at the same time.

## [0.9.0] - 2020-08-26

### Added

* Added: Hybrid component conversion support for: ParticleSystem and Volume+collider pairs (local volumes).
* Hybrid component conversion support for: ParticleSystem and Volume+collider pairs (local volumes).

### Fixed

* Fixed parallel for checking errors in Hybrid Renderer jobs.
* Fixed parallel for checking errors in occlusion jobs.


## [0.8.0] - 2020-08-04


### Changed

* Changed SkinnedMeshRendererConversion to take the RootBone into account. The render entities are now parented to the RootBone entity instead of the SkinnedMeshRenderer GameObject entity. As a result the RenderBounds will update correctly when the root bone is transformed.
* Changed SkinnedMeshRendererConversion to compute the SkinMatrices in SkinnedMeshRenderer's root bone space instead of worldspace.

### Fixed

* Fixed the Hybrid V2 uploading code not supporting more than 65535 separate upload operations per frame.
* Fixed render bounds being offset on converted SkinnedMeshRenderers.
* Partially fixed editor picking for Hybrid V2. Picking should now work in simple cases.
* Fixed a memory leak in the HeapAllocator class used by Hybrid Renderer.



## [0.7.0] - 2020-07-10

### Added

* Added support for controling persistent GPU buffer sizes through project settings

### Changed

* Updated minimum Unity Editor version to 2020.1.0b15 (40d9420e7de8)

### Fixed

* Improved hashing of the RenderMesh component.
* Fixed blendshapes getting applied with incorrect weights when the blendshapes are sparse.

### Known Issues

* This version is not compatible with 2020.2.0a17. Please update to the forthcoming alpha.


## [0.6.0] - 2020-05-27

### Added

* Added support for Mesh Deformations using compute shaders.
* Added support for sparse Blendshapes in the compute deformation system.
* Added support for Skinning using sparse bone weights with n number of influences in the compute deformation system.
* Added support for storing matrices as 3x4 on the GPU side. This will used for SRP 10.x series of packages and up.
* Added support for ambient probe environment lighting in URP.

### Changed

* Updated minimum Unity Editor version to 2020.1.0b9 (9c0aec301c8d)

### Fixed

* Fix floating point precision issue in vertex shader skinning.
* Fixed culling of hybrid lights in SceneView when using LiveLink (on 2020.1).

## [0.5.1] - 2020-05-04

### Changed

* Updated dependencies of this package.


## [0.5.0] - 2020-04-24

### Changed

Changes that only affect *Hybrid Renderer V2*:
* V2 now computes accurate AABBs for batches.
* V2 now longer adds WorldToLocal component to renderable entities.

Changes that affect both versions:
* Updated dependencies of this package.

### Deprecated

* Deprecated `FrozenRenderSceneTagProxy` and `RenderMeshProxy`. Please use the GameObject-to-Entity conversion workflow instead.

### Fixed

* Improved precision of camera frustum plane calculation in FrustumPlanes.FromCamera.
* Improved upload performance by uploading matrices as 4x3 instead of 4x4 as well as calculating inverses on the GPU
* Fixed default color properties being in the wrong color space


## [0.4.2] - 2020-04-15

### Changes

* Updated dependencies of this package.


## [0.4.1] - 2020-04-08

### Added (Hybrid V2)

* DisableRendering tag component for disabling rendering of entities

### Changed

* Improved hybrid.renderer landing document. Lots of new information.

### Fixed

* Fixed shadow mapping issues, especially when using the built-in renderer.

### Misc

* Highlighting additional changes introduced in `0.3.4-preview.24` which were not part of the previous changelogs, see below.


## [0.4.0] - 2020-03-13

### Added (All Versions)

* HeapAllocator: Offset allocator for sub-allocating resources such as NativeArrays or ComputeBuffers.

### Added (Hybrid V2)

Hybrid Renderer V2 is a new experimental renderer. It has a significantly higher performance and better feature set compared to the existing hybrid renderer. However, it is not yet confirmed to work on all platforms. To enable Hybrid Renderer V2, use the `ENABLE_HYBRID_RENDERER_V2` define in the Project Settings.

* HybridHDRPSamples Project for sample Scenes, unit tests and graphics tests.
* HybridURPSamples Project for sample Scenes, unit tests and graphics tests.
* MaterialOverride component: User friendly way to configure material overrides for shader properties.
* MaterialOverrideAsset: MaterialOverride asset for configuring general material overrides tied to a shader.
* SparseUploader: Delta update ECS data on GPU ComputeBuffer.
* Support for Unity built-in material properties: See BuiltinMaterialProperties directory for all IComponentData structs.
* Support for HDRP material properties: See HDRPMaterialProperties directory for all IComponentData structs.
* Support for URP material properties: See URPMaterialProperties directory for all IComponentData structs.
* New API (2020.1) to directly write to ComputeBuffer from parallel Burst jobs.
* New API (2020.1) to render Hybrid V2 batches though optimized SRP Batcher backend.

### Changes (Hybrid V2)

* Full rewrite of RenderMeshSystemV2 and InstancedRenderMeshBatchGroup. New code is located at `HybridV2RenderSystem.cs`.
* Partial rewrite of culling. Now all culling code is located at `HybridV2Culling.cs`.
* Hybrid Renderer and culling no longer use hash maps or IJobNativeMultiHashMapVisitKeyMutableValue jobs. Chunk components and chunk/forEach jobs are used instead.
* Batch setup and update now runs in parallel Burst jobs. Huge performance benefit.
* GPU persistent data model. ComputeBuffer to store persistent data on GPU side. Use `chunk.DidChange<T>` to delta update only changed data. Huge performance benefit.
* Per-instance shader constants are no longer setup to constant buffers for each viewport. This makes HDRP script main thread cost significantly smaller and saves significant amount of CPU time in render thread.

### Fixed

* Fixed culling issues (disappearing entities) 8000+ meters away from origin.
* Fixes to solve chunk fragmentation issues with ChunkWorldRenderBounds and other chunk components. Some changes were already included in 0.3.4 package, but not documented.
* Removed unnecessary reference to Unity.RenderPipelines.HighDefinition.Runtime from asmdef.
* Fixed uninitialized data issues causing flickering on some graphics backends (2020.1).

### Misc

* Highlighting `RenderBounds` component change introduced in `0.3.4-preview.24` which was not part of the previous changelogs, see below.


## [0.3.5] - 2020-03-03

### Changed

* Updated dependencies of this package.


## [0.3.4] - 2020-02-17

### Changed

* Updated dependencies of this package.
* When creating entities from scratch with code, user now needs to manually add `RenderBounds` component. Instantiating prefab works as before.
* Inactive GameObjects and Prefabs with `StaticOptimizeEntity` are now correctly treated as static
* `RenderBoundsUpdateSystem` is no longer `public` (breaking)
* deleted public `CreateMissingRenderBoundsFromMeshRenderer` system (breaking)


## [0.3.3] - 2020-01-28

### Changed

* Updated dependencies of this package.


## [0.3.2] - 2020-01-16

### Changed

* Updated dependencies of this package.


## [0.3.1] - 2019-12-16

**This version requires Unity 2019.3.0f1+**

### Changes

* Updated dependencies of this package.


## [0.3.0] - 2019-12-03

### Changes

* Updated dependencies of this package.


## [0.2.0] - 2019-11-22

**This version requires Unity 2019.3 0b11+**

### New Features

* Added support for vertex skinning.

### Fixes

* Fixed an issue where disabled UnityEngine Components were not getting ignored when converted via `ConvertToEntity` (it only was working for subscenes).

### Changes

* Removed `LightSystem` and light conversion.
* Updated dependencies for this package.

### Upgrade guide

  * `Lightsystem` was not performance by default and the concept of driving a game object from a component turned out to be not performance by default. It was also not maintainable because every property added to lights has to be reflected in this package.
  * `LightSystem` will be replaced with hybrid entities in the future. This will be a more clean uniform API for graphics related functionalities.


## [0.1.1] - 2019-08-06

### Fixes

* Adding a disabled tag component, now correctly disables the light.

### Changes

* Updated dependencies for this package.


## [0.1.0] - 2019-07-30

### New Features

* New `GameObjectConversionSettings` class that we are using to help manage the various and growing settings that can tune a GameObject conversion.
* New ability to convert and export Assets, which is initially needed for Tiny.
  * Assets are discovered via `DeclareReferencedAsset` in the `GameObjectConversionDeclareObjectsGroup` phase and can then be converted by a System during normal conversion phases.
  * Assets can be marked for export and assigned a guid via `GameObjectConversionSystem.GetGuidForAssetExport`. During the System `GameObjectExportGroup` phase, the converted assets can be exported via `TryCreateAssetExportWriter`.
* `GetPrimaryEntity`, `HasPrimaryEntity`, and the new `TryGetPrimaryEntity` all now work on `UnityEngine.Object` instead of `GameObject` so that they can also query against Unity Assets.

### Upgrade guide

* Various GameObject conversion-related methods now receive a `GameObjectConversionSettings` object rather than a set of misc config params.
  * `GameObjectConversionSettings` has implicit constructors for common parameters such as `World`, so much existing code will likely just work.
  * Otherwise construct a `GameObjectConversionSettings`, configure it with the parameters you used previously, and send it in.
* `GameObjectConversionSystem`: `AddLinkedEntityGroup` is now `DeclareLinkedEntityGroup` (should auto-upgrade).
* The System group `GameObjectConversionDeclarePrefabsGroup` is now `GameObjectConversionDeclareObjectsGroup`. This cannot auto-upgrade but a global find&replace will fix it.
* `GameObjectConversionUtility.ConversionFlags.None` is gone, use 0 instead.

### Changes

* Changing `entities` dependency to latest version (`0.1.0-preview`).


## [0.0.1-preview.13] - 2019-05-24

### Changes

* Changing `entities` dependency to latest version (`0.0.12-preview.33`).


## [0.0.1-preview.12] - 2019-05-16

### Fixes

* Adding/fixing `Equals` and `GetHashCode` for proxy components.


## [0.0.1-preview.11] - 2019-05-01

Change tracking started with this version.
