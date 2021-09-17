using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Rendering
{
    [MaterialProperty("unity_SHBr"                    , MaterialPropertyFormat.Float4)]
    [GenerateAuthoringComponent]
    public struct BuiltinMaterialPropertyUnity_SHBr : IComponentData { public float4   Value; }
}
