using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_Smoothness"           , MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct HDRPMaterialPropertySmoothness : IComponentData { public float  Value; }
}
