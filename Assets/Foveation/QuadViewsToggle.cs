using System.Collections;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace Foveation
{
    /// <summary>
    /// Switches quad views on and off while the app is running.
    ///
    /// The view configuration (stereo or quad views) is chosen when the OpenXR session is created and an
    /// OpenXR feature cannot be enabled or disabled while OpenXR is running. So a toggle stops XR, changes
    /// <see cref="QuadViewsFoveationFeature"/>, and starts XR again. The headset shows black for a moment.
    /// </summary>
    public class QuadViewsToggle : MonoBehaviour
    {
        [Tooltip("Key that switches quad views on and off.")]
        [SerializeField]
        Key m_ToggleKey = Key.Digit1;

        [Tooltip("Tick or untick in Play Mode to switch quad views. Shows the current state.")]
        [SerializeField]
        bool m_QuadViews = true;

        bool m_Applied;
        bool m_Restarting;

#if UNITY_EDITOR
        // Changing a feature in Play Mode also changes the settings asset, so the original value is put back afterwards.
        static bool? s_EditorOriginalEnabled;
#endif

        static QuadViewsFoveationFeature feature =>
            OpenXRSettings.Instance != null ? OpenXRSettings.Instance.GetFeature<QuadViewsFoveationFeature>() : null;

        void Start()
        {
            if (feature == null)
            {
                Debug.LogWarning("[QuadViews] Quad Views feature not found in the OpenXR settings. Run Tools → Foveated Rendering → Configure Eye-Tracked Quad Views (Vive Pro Eye).");
                enabled = false;
                return;
            }

            m_QuadViews = m_Applied = feature.enabled;
            Debug.Log($"[QuadViews] Started with quad views {(m_Applied ? "ON" : "OFF")}. Press {m_ToggleKey} to switch.");
        }

        void Update()
        {
            // Without a running headset the key switches the desktop simulation (QuadViewsSimulator) instead.
            var keyboard = Keyboard.current;
            var xrRunning = XRGeneralSettings.Instance != null && XRGeneralSettings.Instance.Manager != null &&
                XRGeneralSettings.Instance.Manager.isInitializationComplete;
            if (keyboard != null && keyboard[m_ToggleKey].wasPressedThisFrame && !m_Restarting && xrRunning)
                m_QuadViews = !m_QuadViews;

            if (m_QuadViews != m_Applied && !m_Restarting)
                StartCoroutine(RestartXR(m_QuadViews));
        }

        IEnumerator RestartXR(bool quadViews)
        {
            m_Restarting = true;
            Debug.Log($"[QuadViews] Switching quad views {(quadViews ? "ON" : "OFF")}, restarting XR...");

            var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
            if (manager == null)
            {
                Debug.LogError("[QuadViews] XR Plug-in Management is not set up.");
                m_QuadViews = m_Applied;
                m_Restarting = false;
                yield break;
            }

            if (manager.isInitializationComplete)
            {
                manager.StopSubsystems();
                manager.DeinitializeLoader();
            }

            // Let the session shut down before the next one is created.
            yield return null;

#if UNITY_EDITOR
            if (s_EditorOriginalEnabled == null)
            {
                s_EditorOriginalEnabled = feature.enabled;
                EditorApplication.playModeStateChanged += RestoreFeatureAfterPlayMode;
            }
#endif
            feature.enabled = quadViews;

            yield return manager.InitializeLoader();

            if (manager.activeLoader == null)
            {
                Debug.LogError("[QuadViews] XR failed to start again. Check that the headset and SteamVR are running.");
            }
            else
            {
                manager.StartSubsystems();
                Debug.Log($"[QuadViews] XR restarted. Quad views requested: {quadViews}, active: {QuadViewsFoveationFeature.isQuadViewsEnabled}");
            }

            m_Applied = quadViews;
            m_Restarting = false;
        }

#if UNITY_EDITOR
        static void RestoreFeatureAfterPlayMode(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredEditMode)
                return;

            EditorApplication.playModeStateChanged -= RestoreFeatureAfterPlayMode;
            if (s_EditorOriginalEnabled is bool original && feature != null)
                feature.enabled = original;
            s_EditorOriginalEnabled = null;
        }
#endif
    }
}
