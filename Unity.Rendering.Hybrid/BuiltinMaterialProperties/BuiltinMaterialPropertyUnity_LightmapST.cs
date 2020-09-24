using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Rendering
{
    [MaterialProperty("unity_LightmapST"              , MaterialPropertyFormat.Float4)] public struct BuiltinMaterialPropertyUnity_LightmapST : IComponentData { public float4   Value; }
}