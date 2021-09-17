using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Rendering
{
    [MaterialProperty("unity_SHAb"                    , MaterialPropertyFormat.Float4)]
    [GenerateAuthoringComponent]
    public struct BuiltinMaterialPropertyUnity_SHAb : IComponentData { public float4   Value; }
}
