using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_Thickness"            , MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct HDRPMaterialPropertyThickness : IComponentData { public float  Value; }
}
