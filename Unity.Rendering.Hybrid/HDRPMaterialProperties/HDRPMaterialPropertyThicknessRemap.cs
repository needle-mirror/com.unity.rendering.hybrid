using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Rendering
{
    [MaterialProperty("_ThicknessRemap"       , MaterialPropertyFormat.Float4)]
    [GenerateAuthoringComponent]
    public struct HDRPMaterialPropertyThicknessRemap : IComponentData { public float4 Value; }
}
