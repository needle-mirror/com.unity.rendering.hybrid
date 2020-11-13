# Runtime entity creation

To render an entity, Hybrid Renderer requires that the entity contains a specific minimum set of components. The list of components Hybrid Renderer requires is substantial and may change in the future. To allow you to flexibly create entities at runtime in a way that is consistent between package versions, Hybrid Renderer provides the `RenderMeshUtility.AddComponents` API.

## RenderMeshUtility - AddComponents

This API takes an entity and adds the components Hybrid Renderer requires based on a `RenderMeshDescription`, which is a struct that describes how to render the entity. A `RenderMeshDescription` includes a `RenderMesh` and additional rendering settings. To create a `RenderMeshDescription`, there are two constructors:

- The first uses a [Renderer](https://docs.unity3d.com/ScriptReference/Renderer.html) and a [Mesh](https://docs.unity3d.com/ScriptReference/Mesh.html) then builds the description from that.
- The second uses properties that you explicitly declare.

This API tries to be as efficient as possible, but it is still a main-thread only API and therefore not suitable for creating a large number of entities. Instead, it is best practice to use `Instantiate` to efficiently clone existing entities then set their components (e.g. `Translation` or `LocalToWorld`) to new values afterward. This workflow has several advantages:

- You can convert the base entity from a Prefab, or create it at runtime using `RenderMeshUtility.AddComponents`. Instantiation performance does not depend on which approach you use.
- `Instantiate` and `SetComponent` / `SetComponentData` don't cause resource-intensive structural changes.
- You can use `Instantiate` and `SetComponent` from Burst jobs using `EntityCommandBuffer.ParallelWriter`, which efficiently scales to multiple cores.
- Internal Hybrid Renderer components are pre-created for the entities, which means that Hybrid Renderer does not need to create those components at runtime.

### Example usage

The following code example shows how to use `RenderMeshUtility.AddComponents` to create a base entity and then instantiate that entity many times in a Burst job:

```c#
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

public class AddComponentsExample : MonoBehaviour
{
    public Mesh Mesh;
    public Material Material;
    public int EntityCount;

    // Example Burst job that creates many entities
    [BurstCompatible]
    public struct SpawnJob : IJobParallelFor
    {
        public Entity Prototype;
        public int EntityCount;
        public EntityCommandBuffer.ParallelWriter Ecb;

        public void Execute(int index)
        {
            // Clone the Prototype entity to create a new entity.
            var e = Ecb.Instantiate(index, Prototype);
            // Prototype has all correct components up front, can use SetComponent to
            // set values unique to the newly created entity, such as the transform.
            Ecb.SetComponent(index, e, new LocalToWorld {Value = ComputeTransform(index)});
        }

        public float4x4 ComputeTransform(int index)
        {
            return float4x4.Translate(new float3(index, 0, 0));
        }
    }

    void Start()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        var entityManager = world.EntityManager;

        EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.TempJob);

        // Create a RenderMeshDescription using the convenience constructor
        // with named parameters.
        var desc = new RenderMeshDescription(
            Mesh,
            Material,
            shadowCastingMode: ShadowCastingMode.Off,
            receiveShadows: false);

        // Create empty base entity
        var prototype = entityManager.CreateEntity();

        // Call AddComponents to populate base entity with the components required
        // by Hybrid Renderer
        RenderMeshUtility.AddComponents(
            prototype,
            entityManager,
            desc);
        entityManager.AddComponentData(prototype, new LocalToWorld());

        // Spawn most of the entities in a Burst job by cloning a pre-created prototype entity,
        // which can be either a Prefab or an entity created at run time like in this sample.
        // This is the fastest and most efficient way to create entities at run time.
        var spawnJob = new SpawnJob
        {
            Prototype = prototype,
            Ecb = ecb.AsParallelWriter(),
            EntityCount = EntityCount,
        };

        var spawnHandle = spawnJob.Schedule(EntityCount, 128);
        spawnHandle.Complete();

        ecb.Playback(entityManager);
        ecb.Dispose();
        entityManager.DestroyEntity(prototype);
    }
}
```

