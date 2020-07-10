using Unity.Entities;

namespace Unity.Rendering
{
    internal struct SkinningTag : IComponentData { }

    [MaterialProperty("_SkinMatrixIndex", MaterialPropertyFormat.Float)]
    internal struct SkinMatrixBufferIndex : IComponentData
    {
        public int Value;
    }
}
