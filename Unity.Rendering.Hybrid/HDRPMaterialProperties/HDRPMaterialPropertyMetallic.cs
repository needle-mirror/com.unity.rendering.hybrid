using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_Metallic"             , MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct HDRPMaterialPropertyMetallic : IComponentData { public float  Value; }
}
