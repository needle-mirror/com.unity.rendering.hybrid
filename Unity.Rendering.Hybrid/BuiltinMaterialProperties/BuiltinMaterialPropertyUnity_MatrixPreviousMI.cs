using Unity.Entities;
using Unity.Mathematics;

#if ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_1_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)
namespace Unity.Rendering
{
    [MaterialProperty("unity_MatrixPreviousMI"        , MaterialPropertyFormat.Float4x4)] public struct BuiltinMaterialPropertyUnity_MatrixPreviousMI         : IComponentData { public float4x4 Value; }
}
#endif
