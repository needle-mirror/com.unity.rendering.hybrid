using System.Collections.Generic;
using Unity.Transforms;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.VFX;
#if HDRP_7_0_0_OR_NEWER
using UnityEngine.Rendering.HighDefinition;
#endif
#if URP_7_0_0_OR_NEWER
using UnityEngine.Rendering.Universal;
#endif

namespace Unity.Rendering
{
#if !TINY_0_22_0_OR_NEWER

    [ConverterVersion("sebbi", 1)]
    [WorldSystemFilter(WorldSystemFilterFlags.HybridGameObjectConversion)]
    class HybridEntitiesConversion : GameObjectConversionSystem
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            InitEntityQueryCache(20);       // To avoid debug log error about GC
        }

        protected override void OnUpdate()
        {
            Entities.ForEach((Light light) =>
            {
                AddHybridComponent(light);
                var entity = GetPrimaryEntity(light);
                ConfigureEditorRenderData(entity, light.gameObject, true);

#if UNITY_2020_2_OR_NEWER && UNITY_EDITOR
                // Explicitly store the LightBakingOutput using a component, so we can restore it
                // at runtime.
                var bakingOutput = light.bakingOutput;
                DstEntityManager.AddComponentData(entity, new LightBakingOutputData {Value = bakingOutput});
#endif
            });

            Entities.ForEach((LightProbeProxyVolume group) =>
            {
                AddHybridComponent(group);
            });

            Entities.ForEach((ReflectionProbe probe) =>
            {
                AddHybridComponent(probe);
            });

            Entities.ForEach((TextMesh mesh, MeshRenderer renderer) =>
            {
                AddHybridComponent(mesh);
                AddHybridComponent(renderer);
            });

            Entities.ForEach((SpriteRenderer sprite) =>
            {
                AddHybridComponent(sprite);
            });

            Entities.ForEach((VisualEffect vfx) =>
            {
                AddHybridComponent(vfx);
            });

            Entities.ForEach((ParticleSystem ps, ParticleSystemRenderer ren) =>
            {
                AddHybridComponent(ps);
                AddHybridComponent(ren);
            });

#if SRP_7_0_0_OR_NEWER
            Entities.ForEach((Volume volume) =>
            {
                AddHybridComponent(volume);
            });

            // NOTE: Colliders are only converted when a graphics Volume is on the same GameObject to avoid problems with Unity Physics!
            Entities.ForEach((SphereCollider collider, Volume volume) =>
            {
                AddHybridComponent(collider);
            });

            Entities.ForEach((BoxCollider collider, Volume volume) =>
            {
                AddHybridComponent(collider);
            });

            Entities.ForEach((CapsuleCollider collider, Volume volume) =>
            {
                AddHybridComponent(collider);
            });

            Entities.ForEach((MeshCollider collider, Volume volume) =>
            {
                AddHybridComponent(collider);
            });
#endif

#if HDRP_7_0_0_OR_NEWER
            // HDRP specific extra data for Light
            Entities.ForEach((HDAdditionalLightData light) =>
            {
#if UNITY_2020_2_OR_NEWER && UNITY_EDITOR
                if (light.GetComponent<Light>().lightmapBakeType != LightmapBakeType.Baked)
#endif
                    AddHybridComponent(light);
            });

            // HDRP specific extra data for ReflectionProbe
            Entities.ForEach((HDAdditionalReflectionData reflectionData) =>
            {
                AddHybridComponent(reflectionData);
            });

            Entities.ForEach((DecalProjector projector) =>
            {
                AddHybridComponent(projector);
            });

            Entities.ForEach((DensityVolume volume) =>
            {
                AddHybridComponent(volume);
            });

            Entities.ForEach((PlanarReflectionProbe probe) =>
            {
                AddHybridComponent(probe);
            });

//This feature requires a modified HDRP
//If ProbeVolumes are enabled, add PROBEVOLUME_CONVERSION
//to the project script defines
#if PROBEVOLUME_CONVERSION
            Entities.ForEach((ProbeVolume probe) =>
            {
                AddHybridComponent(probe);
            });
#endif
#endif

#if URP_7_0_0_OR_NEWER
            // URP specific extra data for Light
            Entities.ForEach((UniversalAdditionalLightData light) =>
            {
#if UNITY_2020_2_OR_NEWER && UNITY_EDITOR
                if (light.GetComponent<Light>().lightmapBakeType != LightmapBakeType.Baked)
#endif
                    AddHybridComponent(light);
            });
#endif

#if HYBRID_ENTITIES_CAMERA_CONVERSION
            // Camera conversion is disabled by default, because Unity Editor loses track of the main camera if it's put into a subscene
            Entities.ForEach((Camera camera) =>
            {
                AddHybridComponent(camera);
            });

#if HDRP_7_0_0_OR_NEWER
            Entities.ForEach((HDAdditionalCameraData data) =>
            {
                AddHybridComponent(data);
            });
#endif

#if URP_7_0_0_OR_NEWER
            Entities.ForEach((UniversalAdditionalCameraData data) =>
            {
                AddHybridComponent(data);
            });
#endif
#endif
        }
    }

#endif
    }


