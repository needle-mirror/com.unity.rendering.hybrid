using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_SmoothnessRemapMax"   , MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct HDRPMaterialPropertySmoothnessRemapMax : IComponentData { public float  Value; }
}
