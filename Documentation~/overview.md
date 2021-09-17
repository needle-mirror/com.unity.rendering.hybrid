# Hybrid Renderer overview

Hybrid Renderer acts as a bridge between DOTS and Unity's existing rendering architecture. This allows you to use ECS entities instead of GameObjects for significantly improved runtime memory layout and performance in large scenes, while maintaining the compatibility and ease of use of Unity's existing workflows.

## Hybrid Renderer versions

This package contains two versions of Hybrid Renderer. For more information about the different versions of Hybrid Renderer, see [Hybrid Renderer versions](hybrid-renderer-versions.md).

## The GameObject conversion system

Hybrid Renderer includes systems that convert GameObjects into equivalent DOTS entities. You can use these systems to convert the GameObjects in the Unity Editor, or at runtime. Conversion in the Unity Editor results in significantly better scene loading performance.

To convert entites in the Unity Editor, put them in a SubScene. The Unity Editor performs the conversion, and saves the results to disk. To convert your GameObjects to entities at runtime, add a ConvertToEntity component to them.

Unity performs the following steps during conversion:

- The conversion system converts [MeshRenderer](https://docs.unity3d.com/Manual/class-MeshRenderer.html) and[ MeshFilter](https://docs.unity3d.com/Manual/class-MeshFilter.html) components into a DOTS RenderMesh component on the entity. Depending on the render pipeline your Project uses, the conversion system might also add other rendering-related components.
- The conversion system converts[ LODGroup](https://docs.unity3d.com/Manual/class-LODGroup.html) components in GameObject hierarchies to DOTS MeshLODGroupComponents. Each entity referred by the LODGroup component has a DOTS MeshLODComponent.
- The conversion system converts the Transform of each GameObject into a DOTS LocalToWorld component on the entity. Depending on the Transform's properties, the conversion system might also add DOTS Translation, Rotation, and NonUniformScale components.

## Runtime functionality

At runtime, the Hybrid Renderer processes all entities that have LocalToWorld, RenderMesh, and RenderBounds DOTS components. Many HDRP and URP features require their own material property components. These components are added during the MeshRenderer conversion. Processed entities are added to batches. Unity renders the batches using the [SRP Batcher](https://blogs.unity3d.com/2019/02/28/srp-batcher-speed-up-your-rendering/).

Note that if you add entities to your Scene at runtime, it is better to instantiate Prefabs than to create new entities from scratch. Prefabs are already converted to an optimal data layout during DOTS conversion, which results in improved performance. Converted prefabs also automatically contain all the necessary material property components for enabling all supported HDRP and URP features. As Hybrid Renderer frequently adds new features, it is best practice to use the conversion pipeline and prefabs instead of manually building entities from scratch, to avoid compatibility issues when updating to new Hybrid Renderer package versions.