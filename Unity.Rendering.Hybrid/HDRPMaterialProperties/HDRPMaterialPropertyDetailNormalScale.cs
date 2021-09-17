using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_DetailNormalScale"    , MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct HDRPMaterialPropertyDetailNormalScale : IComponentData { public float  Value; }
}
