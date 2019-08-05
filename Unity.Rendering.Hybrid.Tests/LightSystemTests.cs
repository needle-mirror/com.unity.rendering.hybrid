using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
 
#if HDRP_6_EXISTS
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.HDPipeline;
#elif HDRP_7_EXISTS
using UnityEngine.Rendering.HighDefinition;
#endif

namespace Unity.Rendering.Tests
{
    public class LightSystemTests
    {
        [Test]
        public void AddRemoveSetupLights() 
        {
            var world = new World("Test World");

            // Setup a test light
            var entity = world.EntityManager.CreateEntity();

            LocalToWorld localToWorld;
            localToWorld.Value = float4x4.identity;
            world.EntityManager.AddComponentData(entity, localToWorld);

            LightComponent light;
            light.type                      = UnityEngine.LightType.Point;
            light.color                     = new UnityEngine.Color(1, 0, 0, 0);
            light.colorTemperature          = 5000.0f;
            light.range                     = 5.0f;
            light.intensity                 = 1.0f;
            light.cullingMask               = 0;
            light.renderingLayerMask        = 0;
            light.spotAngle                 = 0;
            light.innerSpotAngle            = 0;
            light.shadows                   = UnityEngine.LightShadows.Hard;
            light.shadowCustomResolution    = 256;
            light.shadowNearPlane           = 1.0f;
            light.shadowBias                = 0.0f;
            light.shadowNormalBias          = 0.0f;
            light.shadowStrength            = 1.0f;
            world.EntityManager.AddComponentData(entity, light);

            // Optional dependency to com.unity.render-pipelines.high-definition
#if HDRP_6_EXISTS
            HDLightData hdData;
            hdData.lightTypeExtent = UnityEngine.Experimental.Rendering.HDPipeline.LightTypeExtent.Punctual;
            hdData.intensity = 100.0f;
            hdData.lightDimmer = 1.0f;
            hdData.fadeDistance = 1.0f;
            hdData.affectDiffuse = true;
            hdData.affectSpecular = true;
            hdData.shapeWidth = 5.0f;
            hdData.shapeHeight = 5.0f;
            hdData.aspectRatio = 1.0f;
            hdData.shapeRadius = 5.0f;
            hdData.maxSmoothness = 0.5f;
            hdData.applyRangeAttenuation = true;
            hdData.spotLightShape = UnityEngine.Experimental.Rendering.HDPipeline.SpotLightShape.Cone;
            hdData.enableSpotReflector = false;
            hdData.innerSpotPercent = 50.0f;
            hdData.shadowResolution = 256;
            hdData.shadowDimmer = 1.0f;
            hdData.volumetricShadowDimmer = 1.0f;
            hdData.shadowFadeDistance = 100.0f;
            hdData.contactShadows = false;
            hdData.viewBiasMin = 0.0f;
            hdData.viewBiasMax = 1.0f;
            hdData.viewBiasScale = 1.0f;
            hdData.normalBiasMin = 0.0f;
            hdData.normalBiasMax = 1.0f;
            hdData.normalBiasScale = 1.0f;
            hdData.sampleBiasScale = false;
            hdData.sampleBiasScale = false;
            hdData.edgeLeakFixup = false;
            hdData.edgeToleranceNormal = false;
            hdData.edgeTolerance = 0.0f;
            world.EntityManager.AddComponentData(entity, hdData);
#elif HDRP_7_EXISTS
            HDLightData hdData;
            hdData.lightTypeExtent = LightTypeExtent.Punctual;
            hdData.intensity = 100.0f;
            hdData.lightDimmer = 1.0f;
            hdData.fadeDistance = 1.0f;
            hdData.affectDiffuse = true;
            hdData.affectSpecular = true;
            hdData.shapeWidth = 5.0f;
            hdData.shapeHeight = 5.0f;
            hdData.aspectRatio = 1.0f;
            hdData.shapeRadius = 5.0f;
            hdData.maxSmoothness = 0.5f;
            hdData.applyRangeAttenuation = true;
            hdData.spotLightShape = SpotLightShape.Cone;
            hdData.enableSpotReflector = false;
            hdData.innerSpotPercent = 50.0f;
            hdData.customResolution = 256;
            hdData.shadowDimmer = 1.0f;
            hdData.volumetricShadowDimmer = 1.0f;
            hdData.shadowFadeDistance = 100.0f;
            hdData.contactShadows = false;
            hdData.shadowTint = Color.white;
            hdData.normalBias = 0.0f;
            hdData.constantBias = 0.0f;
            hdData.shadowUpdateMode = ShadowUpdateMode.EveryFrame;

            world.EntityManager.AddComponentData(entity, hdData);
#endif

            var lightSystem = world.CreateSystem<LightSystem>();
            lightSystem.Update();

            var lightGO = GameObject.Find("HybridPooledLight");
            Assert.NotNull(lightGO, "Can't find generated pooled light");

            var unityLight = lightGO.GetComponent<UnityEngine.Light>();
            Assert.NotNull(unityLight, "Can't find generated pooled light component");
            Assert.AreEqual(true, unityLight.enabled, "Pooled light is not enabled");

            Assert.AreEqual(light.type, unityLight.type);
            Assert.AreEqual(light.color, unityLight.color);
            Assert.AreEqual(light.colorTemperature, unityLight.colorTemperature);
            Assert.AreEqual(light.range, unityLight.range);
            //Assert.AreEqual(light.intensity,              unityLight.intensity                );       // HDRP will potentially override this. Not reliable to test!
            Assert.AreEqual(light.cullingMask, unityLight.cullingMask);
            Assert.AreEqual(light.renderingLayerMask, unityLight.renderingLayerMask);
            if (light.type == LightType.Spot)
            {
                Assert.AreEqual(light.spotAngle, unityLight.spotAngle);
                Assert.AreEqual(light.innerSpotAngle, unityLight.innerSpotAngle);
            }
            Assert.AreEqual(light.shadows, unityLight.shadows);
            Assert.AreEqual(light.shadowCustomResolution, unityLight.shadowCustomResolution);
            Assert.AreEqual(light.shadowNearPlane, unityLight.shadowNearPlane);
            Assert.AreEqual(light.shadowBias, unityLight.shadowBias);
            Assert.AreEqual(light.shadowNormalBias, unityLight.shadowNormalBias);
            Assert.AreEqual(light.shadowStrength, unityLight.shadowStrength);

#if HDRP_6_EXISTS
            var unityHDData = lightGO.GetComponent<HDAdditionalLightData>();
            Assert.NotNull(unityHDData, "Can't find generated pooled HDAdditionalLightData component");
            Assert.AreEqual(hdData.lightTypeExtent, unityHDData.lightTypeExtent);
            Assert.AreEqual(hdData.intensity, unityHDData.intensity);
            Assert.AreEqual(hdData.lightDimmer, unityHDData.lightDimmer);
            Assert.AreEqual(hdData.fadeDistance, unityHDData.fadeDistance);
            Assert.AreEqual(hdData.affectDiffuse, unityHDData.affectDiffuse);
            Assert.AreEqual(hdData.affectSpecular, unityHDData.affectSpecular);
            Assert.AreEqual(hdData.shapeWidth, unityHDData.shapeWidth);
            Assert.AreEqual(hdData.shapeHeight, unityHDData.shapeHeight);
            Assert.AreEqual(hdData.aspectRatio, unityHDData.aspectRatio);
            Assert.AreEqual(hdData.shapeRadius, unityHDData.shapeRadius);
            Assert.AreEqual(hdData.maxSmoothness, unityHDData.maxSmoothness);
            Assert.AreEqual(hdData.applyRangeAttenuation, unityHDData.applyRangeAttenuation);
            Assert.AreEqual(hdData.spotLightShape, unityHDData.spotLightShape);
            Assert.AreEqual(hdData.enableSpotReflector, unityHDData.enableSpotReflector);
            Assert.AreEqual(hdData.innerSpotPercent, unityHDData.m_InnerSpotPercent);

            var unityHDShadow = lightGO.GetComponent<AdditionalShadowData>();
            Assert.NotNull(unityHDData, "Can't find generated pooled AdditionalShadowData component");
            Assert.AreEqual(hdData.shadowResolution, unityHDShadow.shadowResolution);
            Assert.AreEqual(hdData.shadowDimmer, unityHDShadow.shadowDimmer);
            Assert.AreEqual(hdData.volumetricShadowDimmer, unityHDShadow.volumetricShadowDimmer);
            Assert.AreEqual(hdData.shadowFadeDistance, unityHDShadow.shadowFadeDistance);
            Assert.AreEqual(hdData.contactShadows, unityHDShadow.contactShadows);
            Assert.AreEqual(hdData.viewBiasMin, unityHDShadow.viewBiasMin);
            Assert.AreEqual(hdData.viewBiasMax, unityHDShadow.viewBiasMax);
            Assert.AreEqual(hdData.viewBiasScale, unityHDShadow.viewBiasScale);
            Assert.AreEqual(hdData.normalBiasMin, unityHDShadow.normalBiasMin);
            Assert.AreEqual(hdData.normalBiasMax, unityHDShadow.normalBiasMax);
            Assert.AreEqual(hdData.normalBiasScale, unityHDShadow.normalBiasScale);
            Assert.AreEqual(hdData.sampleBiasScale, unityHDShadow.sampleBiasScale);
            Assert.AreEqual(hdData.sampleBiasScale, unityHDShadow.sampleBiasScale);
            Assert.AreEqual(hdData.edgeLeakFixup, unityHDShadow.edgeLeakFixup);
            Assert.AreEqual(hdData.edgeToleranceNormal, unityHDShadow.edgeToleranceNormal);
            Assert.AreEqual(hdData.edgeTolerance, unityHDShadow.edgeTolerance);
#elif HDRP_7_EXISTS
            var unityHDData = lightGO.GetComponent<HDAdditionalLightData>();
            Assert.NotNull(unityHDData, "Can't find generated pooled HDAdditionalLightData component");
            Assert.AreEqual(hdData.lightTypeExtent, unityHDData.lightTypeExtent);
            Assert.AreEqual(hdData.intensity, unityHDData.intensity);
            Assert.AreEqual(hdData.lightDimmer, unityHDData.lightDimmer);
            Assert.AreEqual(hdData.fadeDistance, unityHDData.fadeDistance);
            Assert.AreEqual(hdData.affectDiffuse, unityHDData.affectDiffuse);
            Assert.AreEqual(hdData.affectSpecular, unityHDData.affectSpecular);
            Assert.AreEqual(hdData.shapeWidth, unityHDData.shapeWidth);
            Assert.AreEqual(hdData.shapeHeight, unityHDData.shapeHeight);
            Assert.AreEqual(hdData.aspectRatio, unityHDData.aspectRatio);
            Assert.AreEqual(hdData.shapeRadius, unityHDData.shapeRadius);
            Assert.AreEqual(hdData.maxSmoothness, unityHDData.maxSmoothness);
            Assert.AreEqual(hdData.applyRangeAttenuation, unityHDData.applyRangeAttenuation);
            Assert.AreEqual(hdData.spotLightShape, unityHDData.spotLightShape);
            Assert.AreEqual(hdData.enableSpotReflector, unityHDData.enableSpotReflector);
            Assert.AreEqual(hdData.innerSpotPercent, unityHDData.innerSpotPercent);
            Assert.AreEqual(hdData.customResolution, unityHDData.customResolution);
            Assert.AreEqual(hdData.shadowDimmer, unityHDData.shadowDimmer);
            Assert.AreEqual(hdData.volumetricShadowDimmer, unityHDData.volumetricShadowDimmer);
            Assert.AreEqual(hdData.shadowFadeDistance, unityHDData.shadowFadeDistance);
            Assert.AreEqual(hdData.contactShadows, unityHDData.contactShadows);
            Assert.AreEqual(hdData.shadowTint, unityHDData.shadowTint);
            Assert.AreEqual(hdData.normalBias, unityHDData.normalBias);
            Assert.AreEqual(hdData.constantBias, unityHDData.constantBias);
            Assert.AreEqual(hdData.shadowUpdateMode, unityHDData.shadowUpdateMode);
#endif

            // Test light delete
            world.EntityManager.DestroyEntity(entity);
            lightSystem.Update();

            Assert.AreEqual(false, unityLight.enabled, "Pooled light is deleted, but not disabled properly!");

            world.DestroySystem(lightSystem);
            world.Dispose();
        }
    }
}
