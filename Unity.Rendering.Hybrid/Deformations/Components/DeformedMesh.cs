using Unity.Entities;

namespace Unity.Rendering
{

#if ENABLE_COMPUTE_DEFORMATIONS
    /// <summary>
    /// Matieral property that contains the index of where the mesh data (por,nrm,tan) 
    /// for a deformed entity starts in the Mesh Instance buffer
    /// </summary>
    [MaterialProperty("_ComputeMeshIndex", MaterialPropertyFormat.Float)]
    internal struct DeformedMeshIndex : IComponentData
    {
        public uint Value;
    }
#endif

    /// <summary>
    /// Used by render mesh entities to retrieve the skinned (deformed) entity which contains the data.
    /// </summary>
    internal struct DeformedEntity : IComponentData
    {
        public Entity Value;
    }
}
