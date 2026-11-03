using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace Foveation.Editor
{
    /// <summary>
    /// Configures the project for eye-tracked foveated rendering on HTC Vive Pro Eye (Windows / SteamVR)
    /// using Unity's OpenXR quad views rendering plus the Quad-Views-Foveated API layer.
    /// Uses Unity's own APIs so it is safe to run while the Editor is open.
    /// </summary>
    public static class FoveationProjectSetup
    {
        const string k_MenuPath = "Tools/Foveated Rendering/Configure Eye-Tracked Quad Views (Vive Pro Eye)";

        [MenuItem(k_MenuPath)]
        static void Configure()
        {
            var log = new StringBuilder();

            // 1. The Quad-Views-Foveated layer only supports Direct3D 11; it bypasses itself on D3D12/Vulkan.
            var target = BuildTarget.StandaloneWindows64;
            var apis = PlayerSettings.GetGraphicsAPIs(target);
            var graphicsChanged = PlayerSettings.GetUseDefaultGraphicsAPIs(target) || apis.Length != 1 || apis[0] != GraphicsDeviceType.Direct3D11;
            if (graphicsChanged)
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
                PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D11 });
            }
            log.AppendLine("- Windows graphics API: Direct3D11 only");

            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
            if (settings == null)
            {
                EditorUtility.DisplayDialog("Foveated Rendering", "OpenXR settings for Standalone were not found. Enable OpenXR under XR Plug-in Management first.", "OK");
                return;
            }

            // 2. Single Pass Instanced with quad views has been reported to crash on PC, so use Multi-pass.
            settings.renderMode = OpenXRSettings.RenderMode.MultiPass;
            log.AppendLine("- OpenXR Render Mode: Multi-pass");

            // 3. Quad views replaces the VRS-based foveation, which cannot follow the eyes on SteamVR.
            SetFeature<FoveatedRenderingFeature>(settings, false, log);
            SetFeature<QuadViewsFoveationFeature>(settings, true, log);
            // Vive Pro Eye controllers.
            SetFeature<HTCViveControllerProfile>(settings, true, log);
            // Eye gaze input for the app itself (the layer reads gaze on its own).
            SetFeature<EyeGazeInteraction>(settings, true, log);

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();

            var message = log.ToString();
            if (graphicsChanged)
                message += "\nThe Editor is still running on its old graphics API. Restart Unity so Play Mode also uses Direct3D11.";
            Debug.Log("[Foveation] Project configured:\n" + message);
            EditorUtility.DisplayDialog("Foveated Rendering", message, "OK");
        }

        static void SetFeature<T>(OpenXRSettings settings, bool enabled, StringBuilder log) where T : OpenXRFeature
        {
            var feature = settings.GetFeature<T>();
            if (feature == null)
            {
                log.AppendLine($"- {typeof(T).Name}: not found");
                return;
            }

            feature.enabled = enabled;
            EditorUtility.SetDirty(feature);
            log.AppendLine($"- {typeof(T).Name}: {(enabled ? "enabled" : "disabled")}");
        }
    }
}
