using System.Collections;
using System.Collections.Generic;
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

        [Header("Layer settings in the headset (written to settings.cfg, then XR restarts)")]
        [Tooltip("Larger / smaller eye tracked focus region (horizontal_focus_section and vertical_focus_section).")]
        [SerializeField]
        Key m_FocusLargerKey = Key.PageUp;

        [SerializeField]
        Key m_FocusSmallerKey = Key.PageDown;

        [Tooltip("Higher / lower peripheral resolution (peripheral_multiplier).")]
        [SerializeField]
        Key m_PeripheryHigherKey = Key.Home;

        [SerializeField]
        Key m_PeripheryLowerKey = Key.End;

        [Tooltip("Removes these three settings from settings.cfg, so the layer uses its defaults (0.35, 0.35, 0.5).")]
        [SerializeField]
        Key m_ResetKey = Key.Backspace;

        [Tooltip("The XR restart waits this long after the last key press, so several steps restart XR once.")]
        [SerializeField]
        float m_ApplyDelay = 0.6f;

        const string k_HorizontalFocus = "horizontal_focus_section";
        const string k_VerticalFocus = "vertical_focus_section";
        const string k_Peripheral = "peripheral_multiplier";
        const float k_DefaultFocus = 0.35f;
        const float k_DefaultPeripheral = 0.5f;
        const float k_FocusStep = 0.05f;
        const float k_PeripheralStep = 0.1f;

        /// <summary>Focus region (fraction of the field of view) and peripheral resolution the layer uses, or will use after the pending restart.</summary>
        public static Vector2 focusSection { get; private set; } = new Vector2(k_DefaultFocus, k_DefaultFocus);
        public static float peripheralMultiplier { get; private set; } = k_DefaultPeripheral;

        /// <summary>True while a settings change waits for, or is in, an XR restart.</summary>
        public static bool applying { get; private set; }

        Vector2 m_AppliedFocus;
        float m_AppliedPeripheral;
        float m_ApplyAt = float.PositiveInfinity;

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
            focusSection = m_AppliedFocus = new Vector2(
                QuadViewsLayerSettings.Get(k_HorizontalFocus, k_DefaultFocus), QuadViewsLayerSettings.Get(k_VerticalFocus, k_DefaultFocus));
            peripheralMultiplier = m_AppliedPeripheral = QuadViewsLayerSettings.Get(k_Peripheral, k_DefaultPeripheral);
            applying = false;
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

            if (keyboard != null && xrRunning && !m_Restarting)
                HandleLayerSettingKeys(keyboard);

            if (m_Restarting)
                return;
            if (Time.unscaledTime >= m_ApplyAt)
            {
                m_ApplyAt = float.PositiveInfinity;
                ApplyLayerSettings();
                StartCoroutine(RestartXR(m_QuadViews));
            }
            else if (m_QuadViews != m_Applied)
                StartCoroutine(RestartXR(m_QuadViews));
        }

        void HandleLayerSettingKeys(Keyboard keyboard)
        {
            var focus = focusSection;
            var peripheral = peripheralMultiplier;
            if (keyboard[m_FocusLargerKey].wasPressedThisFrame)
                focus += Vector2.one * k_FocusStep;
            if (keyboard[m_FocusSmallerKey].wasPressedThisFrame)
                focus -= Vector2.one * k_FocusStep;
            if (keyboard[m_PeripheryHigherKey].wasPressedThisFrame)
                peripheral += k_PeripheralStep;
            if (keyboard[m_PeripheryLowerKey].wasPressedThisFrame)
                peripheral -= k_PeripheralStep;
            if (keyboard[m_ResetKey].wasPressedThisFrame)
            {
                focus = new Vector2(k_DefaultFocus, k_DefaultFocus);
                peripheral = k_DefaultPeripheral;
            }

            focus = new Vector2(Mathf.Clamp(Round(focus.x), 0.1f, 0.9f), Mathf.Clamp(Round(focus.y), 0.1f, 0.9f));
            peripheral = Mathf.Clamp(Round(peripheral), 0.1f, 1f);
            if (focus == focusSection && Mathf.Approximately(peripheral, peripheralMultiplier))
                return;

            focusSection = focus;
            peripheralMultiplier = peripheral;
            applying = focus != m_AppliedFocus || !Mathf.Approximately(peripheral, m_AppliedPeripheral);
            m_ApplyAt = applying ? Time.unscaledTime + m_ApplyDelay : float.PositiveInfinity;
        }

        static float Round(float value) => Mathf.Round(value * 100f) / 100f;

        // Default values are removed from the file instead of written, so it stays as short as the user left it.
        void ApplyLayerSettings()
        {
            var values = new Dictionary<string, float?>
            {
                [k_HorizontalFocus] = Mathf.Approximately(focusSection.x, k_DefaultFocus) ? (float?)null : focusSection.x,
                [k_VerticalFocus] = Mathf.Approximately(focusSection.y, k_DefaultFocus) ? (float?)null : focusSection.y,
                [k_Peripheral] = Mathf.Approximately(peripheralMultiplier, k_DefaultPeripheral) ? (float?)null : peripheralMultiplier,
            };
            if (QuadViewsLayerSettings.Set(values))
            {
                m_AppliedFocus = focusSection;
                m_AppliedPeripheral = peripheralMultiplier;
                Debug.Log($"[QuadViews] Layer settings: focus {focusSection.x:0.00} x {focusSection.y:0.00}, periphery {peripheralMultiplier:0.00}. Restarting XR to apply.");
            }
            else
            {
                focusSection = m_AppliedFocus;
                peripheralMultiplier = m_AppliedPeripheral;
            }
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
            applying = false;
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
