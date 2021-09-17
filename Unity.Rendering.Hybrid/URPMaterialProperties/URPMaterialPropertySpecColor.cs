using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Rendering
{
    [MaterialProperty("_SpecColor", MaterialPropertyFormat.Float4)]
    [GenerateAuthoringComponent]
    public struct URPMaterialPropertySpecColor : IComponentData
    {
        public float4 Value;
    }
}
