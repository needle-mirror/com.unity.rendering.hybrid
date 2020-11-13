using System;
using Unity.Entities;

namespace Unity.Rendering
{
    [Serializable]
    public struct FrozenRenderSceneTag : ISharedComponentData, IEquatable<FrozenRenderSceneTag>
    {
        public Hash128          SceneGUID;
        public int              SectionIndex;
        public int              HasStreamedLOD;

        public bool Equals(FrozenRenderSceneTag other)
        {
            return SceneGUID == other.SceneGUID && SectionIndex == other.SectionIndex;
        }

        public override int GetHashCode()
        {
            return SceneGUID.GetHashCode() ^ SectionIndex;
        }

        public override string ToString()
        {
            return $"GUID: {SceneGUID} section: {SectionIndex}";
        }
    }
}
