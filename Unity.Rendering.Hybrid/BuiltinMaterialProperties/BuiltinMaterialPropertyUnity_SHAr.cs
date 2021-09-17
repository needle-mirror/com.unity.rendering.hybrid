using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Rendering
{
    [MaterialProperty("unity_SHAr"                    , MaterialPropertyFormat.Float4)]
    [GenerateAuthoringComponent]
    public struct BuiltinMaterialPropertyUnity_SHAr : IComponentData { public float4   Value; }
}
