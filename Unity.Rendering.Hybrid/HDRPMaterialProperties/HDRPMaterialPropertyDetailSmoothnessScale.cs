using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_DetailSmoothnessScale", MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct HDRPMaterialPropertyDetailSmoothnessScale : IComponentData { public float  Value; }
}
