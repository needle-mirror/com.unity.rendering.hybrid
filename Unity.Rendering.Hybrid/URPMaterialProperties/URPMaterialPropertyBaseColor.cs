using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Rendering
{
    [MaterialProperty("_BaseColor", MaterialPropertyFormat.Float4)]
    [GenerateAuthoringComponent]
    public struct URPMaterialPropertyBaseColor : IComponentData
    {
        public float4 Value;
    }
}
