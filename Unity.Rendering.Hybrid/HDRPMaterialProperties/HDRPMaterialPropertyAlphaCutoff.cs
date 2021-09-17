using Unity.Entities;

namespace Unity.Rendering
{
    [MaterialProperty("_AlphaCutoff"          , MaterialPropertyFormat.Float)]
    [GenerateAuthoringComponent]
    public struct HDRPMaterialPropertyAlphaCutoff : IComponentData { public float  Value; }
}
