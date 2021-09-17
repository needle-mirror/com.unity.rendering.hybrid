using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

[assembly: InternalsVisibleTo("Unity.Rendering.Hybrid.Tests")]
namespace Unity.Rendering
{
    internal static class HybridUtils
    {
        public static EntityQueryDesc GetHybridRenderedQueryDesc()
        {
            return new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ChunkComponentReadOnly<ChunkWorldRenderBounds>(),
                    ComponentType.ReadOnly<WorldRenderBounds>(),
                    ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ReadOnly<RenderMesh>(),
                    ComponentType.ChunkComponent<HybridChunkInfo>(),
                },
            };
        }

        private static bool CheckGLVersion()
        {
            char[] delimiterChars = { ' ', '.'};
            var arr = SystemInfo.graphicsDeviceVersion.Split(delimiterChars);
            if (arr.Length >= 3)
            {
                var major = Int32.Parse(arr[1]);
                var minor = Int32.Parse(arr[2]);

                return major >= 4 && minor >= 3;
            }

            return false;
        }

        public static bool IsHybridSupportedOnSystem()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
                !SystemInfo.supportsComputeShaders ||
                (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore && !CheckGLVersion()))
                return false;

            return true;
        }
    }
}
