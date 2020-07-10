using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Unity.Rendering
{
    public class HybridRendererSettings : ScriptableObject
    {
        private static HybridRendererSettings settingsInstance;

        public const string kHybridRendererSettingsPath = "Assets/HybridRendererSettings.asset";
        private const ulong kDefaultGPUPersistentInstanceDataSizeMB = 128; // 128 MB

        [SerializeField]
        public ulong PersistentGPUMemoryMB = kDefaultGPUPersistentInstanceDataSizeMB;

        public ulong PersistentGpuMemoryBytes => (1024 * 1024 * PersistentGPUMemoryMB);

        private static HybridRendererSettings CreateDefaultSettings()
        {
            var settings = CreateInstance<HybridRendererSettings>();
            settings.PersistentGPUMemoryMB = kDefaultGPUPersistentInstanceDataSizeMB;
            return settings;
        }

        public static HybridRendererSettings GetOrCreateSettings()
        {
#if UNITY_EDITOR
            var settings = AssetDatabase.LoadAssetAtPath<HybridRendererSettings>(kHybridRendererSettingsPath);
            if (settings == null)
            {
                settings = CreateDefaultSettings();
                AssetDatabase.CreateAsset(settings, kHybridRendererSettingsPath);
                AssetDatabase.SaveAssets();

                var preloadedAssets = PlayerSettings.GetPreloadedAssets().ToList();
                preloadedAssets.Add(settings);
                PlayerSettings.SetPreloadedAssets(preloadedAssets.ToArray());
            }
            return settings;
#else
            if (settingsInstance == null)
                settingsInstance = CreateDefaultSettings();
            return settingsInstance;
#endif
        }

        void OnEnable()
        {
            settingsInstance = this;
        }

#if UNITY_EDITOR
        public static SerializedObject GetSerializedSettings()
        {
            return new SerializedObject(GetOrCreateSettings());
        }
#endif
    }
}
