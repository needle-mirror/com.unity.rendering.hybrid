using System.Collections.Generic;
using Unity.Transforms;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.VFX;
#if HDRP_9_0_0_OR_NEWER
using UnityEngine.Rendering.HighDefinition;
#endif

namespace Unity.Rendering
{
#if !TINY_0_22_0_OR_NEWER
    public class LightConversionSystem : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((Light light) =>
            {
                AddHybridComponent(light);
            });
        }
    }
    
#if HDRP_9_0_0_OR_NEWER
    // these cause crash if enabled
    /*public class HDAdditionalLightDataConversionSystem : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((HDAdditionalLightData light) =>
            {
                AddHybridComponent(light);
            });
        }
    }*/
#endif

    public class LightProbeGroupConversionSystem : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((LightProbeGroup group) =>
            {
                AddHybridComponent(group);
            });
        }
    }
    
    public class LightProbeProxyVolumeConversionSystem : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((LightProbeProxyVolume group) =>
            {
                AddHybridComponent(group);
            });
        }
    }
   
    public class ReflectionProbeConversionSystem : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((ReflectionProbe probe) =>
            {
                AddHybridComponent(probe);
            });
        }
    }

#if HDRP_9_0_0_OR_NEWER
    // these cause crash if enabled
    /*public class PlanarReflectionProbeConversionSystem : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            
            Entities.ForEach((PlanarReflectionProbe probe) =>
            {
                AddHybridComponent(probe);
            });
        }
    }*/
#endif

    public class CameraConversionSystem : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((Camera camera) =>
            {
                AddHybridComponent(camera);
            });
        }
    }
    
    public class TextMeshConversionSystem : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((TextMesh mesh) =>
            {
                AddHybridComponent(mesh);
            });
        }
    }
    
    public class SpriteRendererConversionSystem : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((SpriteRenderer sprite) =>
            {
                AddHybridComponent(sprite);
            });
        }
    }
    
    public class VisualEffectConversionSystem : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((VisualEffect vfx) =>
            {
                AddHybridComponent(vfx);
            });
        }
    }
    
    public class ParticleSystemConversionSystem : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((ParticleSystem particleSystem) =>
            {
                AddHybridComponent(particleSystem);
            });
        }
    }
#endif
}
