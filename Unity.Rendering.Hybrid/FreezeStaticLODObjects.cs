using Unity.Entities;
using Unity.Transforms;

namespace Unity.Rendering
{
    [ConverterVersion("unity", 2)]
    [WorldSystemFilter(WorldSystemFilterFlags.EntitySceneOptimizations)]
    [UpdateAfter(typeof(LodRequirementsUpdateSystem))]
    class FreezeStaticLODObjects : ComponentSystem
    {
        protected override void OnUpdate()
        {
            var group = GetEntityQuery(
                new EntityQueryDesc
                {
                    Any = new ComponentType[] { typeof(ActiveLODGroupMask), typeof(MeshLODGroupComponent), typeof(HLODComponent), typeof(MeshLODComponent), typeof(LODGroupWorldReferencePoint) },
                    All = new ComponentType[] { typeof(Static) }
                });

            EntityManager.RemoveComponent(group, new ComponentTypes(typeof(ActiveLODGroupMask), typeof(MeshLODGroupComponent), typeof(HLODComponent), typeof(MeshLODComponent), typeof(LODGroupWorldReferencePoint)));
        }
    }
}
