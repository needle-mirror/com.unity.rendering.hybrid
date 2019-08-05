using System;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
 
#if HDRP_6_EXISTS
using UnityEngine.Experimental.Rendering.HDPipeline;
#elif HDRP_7_EXISTS
using UnityEngine.Rendering.HighDefinition;
#endif

namespace Unity.Rendering
{
    public struct LightComponent : IComponentData
    {
        public LightType type;
        public Color color;
        public float colorTemperature;
        public float range;
        public float intensity;
        public int cullingMask;
        public int renderingLayerMask;

        // Spot specific
        public float spotAngle;
        public float innerSpotAngle;

        // Shadow settings
        public LightShadows shadows;
        public int shadowCustomResolution;
        public float shadowNearPlane;
        public float shadowBias;
        public float shadowNormalBias;
        public float shadowStrength;
    }

    [Serializable]
    public struct LightCookie : ISharedComponentData, IEquatable<LightCookie>
    {
        public UnityEngine.Texture texture;

        public bool Equals(LightCookie other)
        {
            return texture == other.texture;
        }

        public override int GetHashCode()
        {
            return (texture != null ? texture.GetHashCode() : 0);
        }
    }

    // Optional dependency to com.unity.render-pipelines.high-definition
#if HDRP_6_EXISTS
    public struct HDLightData : IComponentData
    {
        public LightTypeExtent lightTypeExtent;

        public float intensity;
        public float lightDimmer;
        public float fadeDistance;
        public bool affectDiffuse;
        public bool affectSpecular;

        public float shapeWidth;
        public float shapeHeight;
        public float aspectRatio;
        public float shapeRadius;
        public float maxSmoothness;
        public bool applyRangeAttenuation;

        // Spot specific
        public SpotLightShape spotLightShape;
        public bool enableSpotReflector;
        public float innerSpotPercent;

        // HDShadowData
        public int shadowResolution;
        public float shadowDimmer;
        public float volumetricShadowDimmer;
        public float shadowFadeDistance;
        public bool contactShadows;
        public Color shadowTint;
        public float normalBias;
        public float constantBias;
        public ShadowUpdateMode shadowUpdateMode;
    }
#elif HDRP_7_EXISTS
    public struct HDLightData : IComponentData
    {
        public LightTypeExtent lightTypeExtent;

        public float intensity;
        public float lightDimmer;
        public float fadeDistance;
        public bool affectDiffuse;
        public bool affectSpecular;

        public float shapeWidth;
        public float shapeHeight;
        public float aspectRatio;
        public float shapeRadius;
        public float maxSmoothness;
        public bool applyRangeAttenuation;

        // Spot specific
        public SpotLightShape spotLightShape;
        public bool enableSpotReflector;
        public float innerSpotPercent;

        // HDShadowData
        public int customResolution;
        public float shadowDimmer;
        public float volumetricShadowDimmer;
        public float shadowFadeDistance;
        public bool contactShadows;
        public Color shadowTint;
        public float normalBias;
        public float constantBias;
        public ShadowUpdateMode shadowUpdateMode;
    }
#endif
}
