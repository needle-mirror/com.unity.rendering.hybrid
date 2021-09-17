using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Rendering
{
    [MaterialProperty("unity_SHAg"                    , MaterialPropertyFormat.Float4)]
    [GenerateAuthoringComponent]
    public struct BuiltinMaterialPropertyUnity_SHAg : IComponentData { public float4   Value; }
}
