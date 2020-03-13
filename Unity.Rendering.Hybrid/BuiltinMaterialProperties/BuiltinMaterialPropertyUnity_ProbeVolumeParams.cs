using Unity.Entities;
using Unity.Mathematics;

#if ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_1_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)
namespace Unity.Rendering
{
    [MaterialProperty("unity_ProbeVolumeParams"       , MaterialPropertyFormat.Float4  )] public struct BuiltinMaterialPropertyUnity_ProbeVolumeParams        : IComponentData { public float4   Value; }
}
#endif
