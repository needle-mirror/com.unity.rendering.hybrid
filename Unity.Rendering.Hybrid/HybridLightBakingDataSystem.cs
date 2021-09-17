using Unity.Entities;
using Unity.Jobs;
using UnityEngine;

namespace Unity.Rendering
{
#if UNITY_2020_2_OR_NEWER
    // Used to explicitly store light baking data at conversion time and restore
    // it at run time, as this doesn't happen automatically with Hybrid entities.
    public struct LightBakingOutputData : IComponentData
    {
        public LightBakingOutput Value;
    }

    public struct LightBakingOutputDataRestoredTag : IComponentData
    {}

    [ExecuteAlways]
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class HybridLightBakingDataSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            Entities
                .WithStructuralChanges()
                .WithNone<LightBakingOutputDataRestoredTag>()
                .ForEach((Entity e, in LightBakingOutputData bakingOutput) =>
                {
                    var light = EntityManager.GetComponentObject<Light>(e);

                    if (light != null)
                        light.bakingOutput = bakingOutput.Value;

                    EntityManager.AddComponent<LightBakingOutputDataRestoredTag>(e);
                }).Run();
        }
    }
#endif
}
