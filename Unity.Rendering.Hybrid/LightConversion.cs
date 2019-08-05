using Unity.Entities;
 
#if HDRP_6_EXISTS
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.HDPipeline;
#elif HDRP_7_EXISTS
using UnityEngine.Rendering.HighDefinition;
#endif

namespace Unity.Rendering
{
    class LightConversion : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((UnityEngine.Light unityLight) =>
            {
                var entity = GetPrimaryEntity(unityLight);

                LightComponent light;
                light.type                      = unityLight.type;
                light.color                     = unityLight.color;
                light.colorTemperature          = unityLight.colorTemperature;
                light.range                     = unityLight.range;
                light.intensity                 = unityLight.intensity;
                light.cullingMask               = unityLight.cullingMask;
                light.renderingLayerMask        = unityLight.renderingLayerMask;
                light.spotAngle                 = unityLight.spotAngle;
                light.innerSpotAngle            = unityLight.innerSpotAngle;
                light.shadows                   = unityLight.shadows;
                light.shadowCustomResolution    = unityLight.shadowCustomResolution;
                light.shadowNearPlane           = unityLight.shadowNearPlane;
                light.shadowBias                = unityLight.shadowBias;
                light.shadowNormalBias          = unityLight.shadowNormalBias;
                light.shadowStrength            = unityLight.shadowStrength;
                DstEntityManager.AddComponentData(entity, light);

                if (unityLight.cookie)
                {
                    LightCookie cookie;
                    cookie.texture = unityLight.cookie;
                    DstEntityManager.AddSharedComponentData(entity, cookie);
                }

                // Optional dependency to com.unity.render-pipelines.high-definition
#if HDRP_6_EXISTS
                HDLightData hdData;

                var unityHdData = unityLight.GetComponent<HDAdditionalLightData>();
                hdData.lightTypeExtent = unityHdData.lightTypeExtent;
                hdData.intensity = unityHdData.intensity;
                hdData.lightDimmer = unityHdData.lightDimmer;
                hdData.fadeDistance = unityHdData.fadeDistance;
                hdData.affectDiffuse = unityHdData.affectDiffuse;
                hdData.affectSpecular = unityHdData.affectSpecular;
                hdData.shapeWidth = unityHdData.shapeWidth;
                hdData.shapeHeight = unityHdData.shapeHeight;
                hdData.aspectRatio = unityHdData.aspectRatio;
                hdData.shapeRadius = unityHdData.shapeRadius;
                hdData.maxSmoothness = unityHdData.maxSmoothness;
                hdData.applyRangeAttenuation = unityHdData.applyRangeAttenuation;
                hdData.spotLightShape = unityHdData.spotLightShape;
                hdData.enableSpotReflector = unityHdData.enableSpotReflector;
                hdData.innerSpotPercent = unityHdData.m_InnerSpotPercent;

                var unityShadowData = unityLight.GetComponent<AdditionalShadowData>();
                hdData.shadowResolution = unityShadowData.shadowResolution;
                hdData.shadowDimmer = unityShadowData.shadowDimmer;
                hdData.volumetricShadowDimmer = unityShadowData.volumetricShadowDimmer;
                hdData.shadowFadeDistance = unityShadowData.shadowFadeDistance;
                hdData.contactShadows = unityShadowData.contactShadows;
                hdData.viewBiasMin = unityShadowData.viewBiasMin;
                hdData.viewBiasMax = unityShadowData.viewBiasMax;
                hdData.viewBiasScale = unityShadowData.viewBiasScale;
                hdData.normalBiasMin = unityShadowData.normalBiasMin;
                hdData.normalBiasMax = unityShadowData.normalBiasMax;
                hdData.normalBiasScale = unityShadowData.normalBiasScale;
                hdData.sampleBiasScale = unityShadowData.sampleBiasScale;
                hdData.edgeLeakFixup = unityShadowData.edgeLeakFixup;
                hdData.edgeToleranceNormal = unityShadowData.edgeToleranceNormal;
                hdData.edgeTolerance = unityShadowData.edgeTolerance;
                DstEntityManager.AddComponentData(entity, hdData);
#elif HDRP_7_EXISTS
                var unityHdData = unityLight.GetComponent<HDAdditionalLightData>();
                HDLightData hdData;
                hdData.lightTypeExtent          = unityHdData.lightTypeExtent;
                hdData.intensity                = unityHdData.intensity;
                hdData.lightDimmer              = unityHdData.lightDimmer;
                hdData.fadeDistance             = unityHdData.fadeDistance;
                hdData.affectDiffuse            = unityHdData.affectDiffuse;
                hdData.affectSpecular           = unityHdData.affectSpecular;
                hdData.shapeWidth               = unityHdData.shapeWidth;
                hdData.shapeHeight              = unityHdData.shapeHeight;
                hdData.aspectRatio              = unityHdData.aspectRatio;
                hdData.shapeRadius              = unityHdData.shapeRadius;
                hdData.maxSmoothness            = unityHdData.maxSmoothness;
                hdData.applyRangeAttenuation    = unityHdData.applyRangeAttenuation;
                hdData.spotLightShape           = unityHdData.spotLightShape;
                hdData.enableSpotReflector      = unityHdData.enableSpotReflector;
                hdData.innerSpotPercent         = unityHdData.innerSpotPercent;
                hdData.customResolution         = unityHdData.customResolution;
                hdData.shadowDimmer             = unityHdData.shadowDimmer;
                hdData.volumetricShadowDimmer   = unityHdData.volumetricShadowDimmer;
                hdData.shadowFadeDistance       = unityHdData.shadowFadeDistance;
                hdData.contactShadows           = unityHdData.contactShadows;
                hdData.shadowTint               = unityHdData.shadowTint;
                hdData.normalBias               = unityHdData.normalBias;
                hdData.constantBias             = unityHdData.constantBias;
                hdData.shadowUpdateMode         = unityHdData.shadowUpdateMode;
                DstEntityManager.AddComponentData(entity, hdData);
#endif
            });
        }
    }
}
