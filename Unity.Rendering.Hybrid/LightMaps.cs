using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Unity.Rendering
{
    public struct LightMaps : ISharedComponentData, IEquatable<LightMaps>
    {
        public Texture2DArray colors;
        public Texture2DArray directions;
        public Texture2DArray shadowMasks;

        public bool hasDirections => directions != null && directions.depth > 0;
        public bool hasShadowMask => shadowMasks != null && shadowMasks.depth > 0;

        public bool isValid => colors != null;

        public bool Equals(LightMaps other)
        {
            return
                colors == other.colors &&
                directions == other.directions &&
                shadowMasks == other.shadowMasks;
        }

        /// <summary>
        /// A representative hash code.
        /// </summary>
        /// <returns>A number that is guaranteed to be the same when generated from two objects that are the same.</returns>
        public override int GetHashCode()
        {
            int hash = 0;
            if (!ReferenceEquals(colors, null)) hash ^= colors.GetHashCode();
            if (!ReferenceEquals(directions, null)) hash ^= directions.GetHashCode();
            if (!ReferenceEquals(shadowMasks, null)) hash ^= shadowMasks.GetHashCode();
            return hash;
        }

        private static Texture2DArray CopyToTextureArray(List<Texture2D> source)
        {
            if (source == null || !source.Any())
                return null;

            var data = source.First();
            if (data == null)
                return null;

            var result = new Texture2DArray(data.width, data.height, source.Count, source[0].graphicsFormat, TextureCreationFlags.MipChain);
            result.filterMode = FilterMode.Trilinear;
            result.wrapMode = TextureWrapMode.Clamp;
            result.anisoLevel = 3;

            for (var sliceIndex = 0; sliceIndex < source.Count; sliceIndex++)
            {
                var lightMap = source[sliceIndex];
                Graphics.CopyTexture(lightMap, 0, result, sliceIndex);
            }

            return result;
        }

        public static LightMaps ConstructLightMaps(List<Texture2D> inColors, List<Texture2D> inDirections, List<Texture2D> inShadowMasks)
        {
            var result = new LightMaps
            {
                colors = CopyToTextureArray(inColors),
                directions = CopyToTextureArray(inDirections),
                shadowMasks = CopyToTextureArray(inShadowMasks)
            };
            return result;
        }
    }
}
