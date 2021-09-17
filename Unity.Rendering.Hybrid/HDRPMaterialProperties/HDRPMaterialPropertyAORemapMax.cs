using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_AORemapMax"           , MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct HDRPMaterialPropertyAORemapMax : IComponentData { public float  Value; }
}
