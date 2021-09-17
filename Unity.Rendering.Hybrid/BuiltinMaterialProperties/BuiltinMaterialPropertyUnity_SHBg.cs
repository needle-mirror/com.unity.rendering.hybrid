using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Rendering
{
    [MaterialProperty("unity_SHBg"                    , MaterialPropertyFormat.Float4)]
    [GenerateAuthoringComponent]
    public struct BuiltinMaterialPropertyUnity_SHBg : IComponentData { public float4   Value; }
}
