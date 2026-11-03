using System;
using System.Collections;
using System.Runtime.InteropServices;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace Foveation
{
    /// <summary>
    /// Requests the XR_VARJO_quad_views extension so Unity's OpenXR plug-in renders the
    /// "primary quad" view configuration (a low-res wide view + a full-res inset per eye).
    ///
    /// On SteamVR + Vive Pro Eye the extension is provided by the Quad-Views-Foveated API layer,
    /// which moves the full-res inset to follow the user's gaze (eye-tracked foveated rendering).
    /// That layer only supports Direct3D 11.
    /// </summary>
#if UNITY_EDITOR
    [UnityEditor.XR.OpenXR.Features.OpenXRFeature(UiName = "Quad Views (Eye-Tracked Foveation)",
        BuildTargetGroups = new[] { BuildTargetGroup.Standalone },
        Company = "vr-example-3",
        Desc = "Requests XR_VARJO_quad_views for eye-tracked foveated rendering through the Quad-Views-Foveated API layer.",
        OpenxrExtensionStrings = k_QuadViewsExtension,
        Version = "1.0.0",
        Category = UnityEditor.XR.OpenXR.Features.FeatureCategory.Feature,
        FeatureId = featureId)]
#endif
    public class QuadViewsFoveationFeature : OpenXRFeature
    {
        public const string featureId = "com.vrexample3.openxr.feature.quadviews";
        const string k_QuadViewsExtension = "XR_VARJO_quad_views";

        // XrStructureType values.
        const int k_TypeSystemProperties = 5;
        const int k_TypeSystemEyeGazeInteractionProperties = 1000030000;
        const int k_TypeSystemEyeTrackingPropertiesFB = 1000202004;

        delegate int GetInstanceProcAddrDelegate(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr function);
        delegate int GetSystemPropertiesDelegate(ulong instance, ulong systemId, IntPtr properties);

        ulong m_Instance;

        /// <summary>True when the runtime (or an API layer) accepted XR_VARJO_quad_views.</summary>
        public static bool isQuadViewsEnabled => OpenXRRuntime.IsExtensionEnabled(k_QuadViewsExtension);

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            m_Instance = xrInstance;

            if (isQuadViewsEnabled)
                Debug.Log("[QuadViews] XR_VARJO_quad_views enabled. Unity renders quad views; the Quad-Views-Foveated layer handles eye tracking.");
            else
                Debug.LogWarning("[QuadViews] XR_VARJO_quad_views is NOT available. Is the Quad-Views-Foveated layer installed and is the app running on Direct3D 11?");

            Debug.Log($"[QuadViews] XR_EXT_eye_gaze_interaction enabled: {OpenXRRuntime.IsExtensionEnabled("XR_EXT_eye_gaze_interaction")}");

            // Keep the session running even without the extension (plain stereo rendering).
            return true;
        }

        protected override void OnSystemChange(ulong xrSystem)
        {
            // Diagnostics: ask the runtime whether it reports eye gaze support, the same way the
            // Quad-Views-Foveated layer does (FB struct first, eye gaze struct second) and the plain way.
            try
            {
                var getProcAddr = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrDelegate>(xrGetInstanceProcAddr);
                getProcAddr(m_Instance, "xrGetSystemProperties", out var function);
                if (function == IntPtr.Zero)
                    return;

                var getSystemProperties = Marshal.GetDelegateForFunctionPointer<GetSystemPropertiesDelegate>(function);
                Debug.Log($"[QuadViews] supportsEyeGazeInteraction (eye gaze struct only): {QueryEyeGazeSupport(getSystemProperties, xrSystem, false)}");
                Debug.Log($"[QuadViews] supportsEyeGazeInteraction (like Quad-Views-Foveated, FB struct first): {QueryEyeGazeSupport(getSystemProperties, xrSystem, true)}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuadViews] Eye gaze support query failed: {e.Message}");
            }
        }

        string QueryEyeGazeSupport(GetSystemPropertiesDelegate getSystemProperties, ulong systemId, bool chainFbStructFirst)
        {
            // Layout: { int type; void* next; uint supports; } and XrSystemProperties fits in 512 bytes.
            var properties = AllocZeroed(512);
            var eyeGaze = AllocZeroed(24);
            var eyeTrackingFb = AllocZeroed(24);
            try
            {
                Marshal.WriteInt32(eyeGaze, 0, k_TypeSystemEyeGazeInteractionProperties);
                Marshal.WriteInt32(eyeTrackingFb, 0, k_TypeSystemEyeTrackingPropertiesFB);
                Marshal.WriteInt32(properties, 0, k_TypeSystemProperties);

                if (chainFbStructFirst)
                {
                    Marshal.WriteIntPtr(properties, 8, eyeTrackingFb);
                    Marshal.WriteIntPtr(eyeTrackingFb, 8, eyeGaze);
                }
                else
                {
                    Marshal.WriteIntPtr(properties, 8, eyeGaze);
                }

                var result = getSystemProperties(m_Instance, systemId, properties);
                if (result != 0)
                    return $"xrGetSystemProperties failed ({result})";

                return Marshal.ReadInt32(eyeGaze, 16) != 0 ? "TRUE" : "false";
            }
            finally
            {
                Marshal.FreeHGlobal(properties);
                Marshal.FreeHGlobal(eyeGaze);
                Marshal.FreeHGlobal(eyeTrackingFb);
            }
        }

        static IntPtr AllocZeroed(int size)
        {
            var ptr = Marshal.AllocHGlobal(size);
            for (var i = 0; i < size; i++)
                Marshal.WriteByte(ptr, i, 0);
            return ptr;
        }

        protected override void OnSessionBegin(ulong xrSession)
        {
            var go = new GameObject("[QuadViews] Eye Gaze Probe");
            DontDestroyOnLoad(go);
            go.AddComponent<EyeGazeProbe>();
        }

        /// <summary>Logs the OpenXR eye gaze pose for a few seconds so we can tell whether gaze data actually arrives.</summary>
        class EyeGazeProbe : MonoBehaviour
        {
            IEnumerator Start()
            {
                for (var i = 0; i < 10; i++)
                {
                    yield return new WaitForSeconds(2f);

                    var device = InputSystem.GetDevice<EyeGazeInteraction.EyeGazeDevice>();
                    if (device == null)
                    {
                        Debug.Log("[QuadViews] Eye gaze device: not found");
                        continue;
                    }

                    var tracked = device.pose.isTracked.ReadValue();
                    var rotation = device.pose.rotation.ReadValue();
                    Debug.Log($"[QuadViews] Eye gaze tracked={tracked} direction={rotation * Vector3.forward}");
                }

                Destroy(gameObject);
            }
        }
    }
}
