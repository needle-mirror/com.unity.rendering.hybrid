using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Unity.Rendering
{
#if UNITY_EDITOR
    static class HybridRendererSettingsRegister
    {
        [SettingsProvider]
        public static SettingsProvider CreateHybridRendererSettingsProvider()
        {
            var provider = new SettingsProvider("Project/Hybrid Renderer Settings", SettingsScope.Project)
            {
                label = "Hybrid Renderer",
                guiHandler = (searchContext) =>
                {
                    var settings = HybridRendererSettings.GetSerializedSettings();
                    EditorGUILayout.PropertyField(settings.FindProperty("PersistentGPUMemoryMB"), new GUIContent("Persistent GPU Buffer Size (MB)"));
                    settings.ApplyModifiedProperties();
                },

                keywords = new HashSet<string>(new[] { "Persistent GPU Buffer Size" })
            };

            return provider;
        }
    }
#endif
}
