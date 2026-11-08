using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine.XR;
using TrackedPoseDriver = UnityEngine.InputSystem.XR.TrackedPoseDriver;

namespace Foveation
{
    public enum QuadViewsSimulationMode
    {
        Off,
        [InspectorName("Fixed (no eye tracking)")]
        Fixed,
        [InspectorName("Eye tracked (simulated gaze)")]
        EyeTracked,
    }

    /// <summary>
    /// Desktop stand-in for quad views while no headset is running, to debug without VR.
    ///
    /// Renders like the Quad-Views-Foveated layer: the main camera draws the whole view at reduced resolution
    /// (peripheral view) and a second camera draws the focus region at full resolution with its own projection.
    /// A shader blends them on screen and can outline the focus region. In eye tracked mode the arrow keys move
    /// the simulated gaze, and the focus region follows it. All settings use the layer's names and defaults and
    /// can be changed in the Inspector at runtime.
    ///
    /// When a headset is running the simulation turns itself off: the real quad views of the layer take over.
    /// </summary>
    public class QuadViewsSimulator : MonoBehaviour
    {
        /// <summary>True while the desktop simulation is rendering.</summary>
        public static bool isSimulating => s_Running != null;

        static QuadViewsSimulator s_Running;

        /// <summary>Whether this component currently allows SSAO (key O), for the performance overlay.</summary>
        public static bool ambientOcclusionEnabled { get; private set; } = true;

        [SerializeField]
        QuadViewsSimulationMode m_Mode = QuadViewsSimulationMode.EyeTracked;

        [Header("Resolution")]
        [Tooltip("peripheral_multiplier: resolution of the peripheral view per axis. Layer default 0.5.")]
        [Range(0.05f, 1f)]
        [SerializeField]
        float m_PeripheralMultiplier = 0.5f;

        [Tooltip("focus_multiplier: resolution of the focus view per axis. 1 = full resolution, above 1 supersamples. Layer default 1.")]
        [Range(0.25f, 2f)]
        [SerializeField]
        float m_FocusMultiplier = 1f;

        [Header("Eye tracked focus region")]
        [Tooltip("horizontal_focus_section: focus width as a fraction of the horizontal field of view. Layer default 0.35.")]
        [Range(0.1f, 0.9f)]
        [SerializeField]
        float m_HorizontalFocusSection = 0.35f;

        [Tooltip("vertical_focus_section: focus height as a fraction of the vertical field of view. Layer default 0.35.")]
        [Range(0.1f, 0.9f)]
        [SerializeField]
        float m_VerticalFocusSection = 0.35f;

        [Tooltip("vertical_focus_offset: added to the gaze; positive moves the region up. Layer default 0.")]
        [Range(-0.5f, 0.5f)]
        [SerializeField]
        float m_VerticalFocusOffset;

        [Header("Fixed focus region")]
        [Tooltip("horizontal_fixed_section: focus width as a fraction of the horizontal field of view. Layer default 0.5.")]
        [Range(0.1f, 0.9f)]
        [SerializeField]
        float m_HorizontalFixedSection = 0.5f;

        [Tooltip("vertical_fixed_section: focus height as a fraction of the vertical field of view. Layer default 0.45.")]
        [Range(0.1f, 0.9f)]
        [SerializeField]
        float m_VerticalFixedSection = 0.45f;

        [Tooltip("vertical_fixed_offset: positive moves the region up. Layer default 0.")]
        [Range(-0.5f, 0.5f)]
        [SerializeField]
        float m_VerticalFixedOffset;

        [Header("Blending")]
        [Tooltip("smoothen_focus_view_edges: width of the blend into the periphery, as a fraction of the focus region. Layer default 0.2.")]
        [Range(0f, 0.5f)]
        [SerializeField]
        float m_SmoothenFocusViewEdges = 0.2f;

        [Tooltip("sharpen_focus_view: contrast adaptive sharpening (AMD CAS) of the focus view. Layer default 0.7.")]
        [Range(0f, 1f)]
        [SerializeField]
        float m_SharpenFocusView = 0.7f;

        [Header("Screen space effects")]
        [Tooltip("Screen Space Ambient Occlusion, on the desktop and in the headset. SSAO runs separately on each view, so the " +
            "focus view gets its own AO: darker smudges near its border and a noise pattern that does not match the periphery. " +
            "Real quad views render the focus view separately too. Turn off to compare. The renderer's own setting is restored on exit.")]
        [SerializeField]
        bool m_ScreenSpaceAmbientOcclusion = true;

        [Tooltip("Renderer whose SSAO feature the option above switches. The original state is restored when the simulation stops.")]
        [SerializeField]
        UniversalRendererData m_Renderer;

        [Header("Simulated gaze")]
        [Tooltip("Gaze point, -1 to 1 on each axis of the view (0, 0 = center). The arrow keys move it.")]
        [SerializeField]
        Vector2 m_Gaze;

        [Tooltip("Gaze speed of the arrow keys, in view widths per second. Hold Shift for 3x.")]
        [SerializeField]
        float m_GazeSpeed = 0.6f;

        [Tooltip("The gaze follows the mouse cursor instead of the arrow keys.")]
        [SerializeField]
        bool m_GazeFollowsMouse;

        [Header("Debug view")]
        [Tooltip("Outline the focus region (green), the start of the blend (yellow dashes) and the gaze point (red), and tint the periphery.")]
        [SerializeField]
        bool m_ShowRegion = true;

        [Range(0f, 0.5f)]
        [SerializeField]
        float m_PeripheryTint = 0.12f;

        [SerializeField]
        bool m_ShowHelp = true;

        [Header("Desktop camera")]
        [Tooltip("Without a headset: hold the right mouse button to look around, WASD to move, E/Q up and down.")]
        [SerializeField]
        bool m_CameraControls = true;

        [SerializeField]
        float m_MoveSpeed = 2f;

        [SerializeField]
        float m_LookSensitivity = 0.12f;

        [Header("Keys")]
        [SerializeField]
        Key m_ToggleKey = Key.Digit1;

        [SerializeField]
        Key m_ModeKey = Key.M;

        [SerializeField]
        Key m_RegionKey = Key.V;

        [SerializeField]
        Key m_CenterKey = Key.C;

        [SerializeField]
        Key m_HelpKey = Key.H;

        [SerializeField]
        Key m_AmbientOcclusionKey = Key.O;

        [SerializeField, HideInInspector]
        Shader m_Shader;

        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;

        QuadViewsSimulationMode m_LastMode = QuadViewsSimulationMode.EyeTracked;
        Camera m_Camera;
        Camera m_FocusCamera;
        Camera m_ClearCamera;
        RenderTexture m_PeripheryTarget;
        RenderTexture m_FocusTarget;
        GameObject m_CanvasRoot;
        RawImage m_Image;
        Material m_Material;
        RectInt m_FocusRect;

        readonly Dictionary<ScriptableRendererFeature, bool> m_OriginalFeatureStates = new Dictionary<ScriptableRendererFeature, bool>();
        TrackedPoseDriver m_DisabledPoseDriver;
        float m_Yaw, m_Pitch;
        bool m_LookInitialized;

        static readonly int k_FocusTex = Shader.PropertyToID("_FocusTex");
        static readonly int k_FocusRect = Shader.PropertyToID("_FocusRect");
        static readonly int k_GazeId = Shader.PropertyToID("_Gaze");
        static readonly int k_Smoothing = Shader.PropertyToID("_Smoothing");
        static readonly int k_Sharpen = Shader.PropertyToID("_Sharpen");
        static readonly int k_ShowRegion = Shader.PropertyToID("_ShowRegion");
        static readonly int k_PeripheryTint = Shader.PropertyToID("_PeripheryTint");
        static readonly int k_ScreenSize = Shader.PropertyToID("_ScreenSizePx");

        public QuadViewsSimulationMode mode
        {
            get => m_Mode;
            set => m_Mode = value;
        }

        void OnValidate()
        {
            if (m_Shader == null)
                m_Shader = Shader.Find("Hidden/Foveation/QuadViewsSimulator");
        }

        void OnDisable()
        {
            StopSimulation();
            RestorePoseDriver();
            RestoreRendererFeatures();
        }

        void Update()
        {
            var xrRunning = IsXRRunning();
            HandleKeys(xrRunning);
            // Also with a headset, to compare the per-view AO of the real quad views.
            ApplyAmbientOcclusion();

            if (xrRunning)
            {
                // The headset drives the camera and the layer renders the real quad views.
                StopSimulation();
                RestorePoseDriver();
                return;
            }

            if (m_CameraControls)
                MoveCamera();
            if (m_GazeFollowsMouse)
                FollowMouse();
        }

        void LateUpdate()
        {
            var shouldRun = m_Mode != QuadViewsSimulationMode.Off && !IsXRRunning() && m_Shader != null;
            if (!shouldRun)
            {
                StopSimulation();
                return;
            }

            if (s_Running != this && !StartSimulation())
                return;
            UpdateViews();
        }

        static bool IsXRRunning()
        {
            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            foreach (var display in displays)
            {
                if (display.running)
                    return true;
            }
            return false;
        }

        void HandleKeys(bool xrRunning)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            // With a headset, the toggle key belongs to QuadViewsToggle (real quad views).
            if (!xrRunning && keyboard[m_ToggleKey].wasPressedThisFrame)
            {
                if (m_Mode == QuadViewsSimulationMode.Off)
                    m_Mode = m_LastMode;
                else
                {
                    m_LastMode = m_Mode;
                    m_Mode = QuadViewsSimulationMode.Off;
                }
            }
            if (keyboard[m_ModeKey].wasPressedThisFrame && m_Mode != QuadViewsSimulationMode.Off)
                m_Mode = m_Mode == QuadViewsSimulationMode.Fixed ? QuadViewsSimulationMode.EyeTracked : QuadViewsSimulationMode.Fixed;
            if (keyboard[m_RegionKey].wasPressedThisFrame)
                m_ShowRegion = !m_ShowRegion;
            if (keyboard[m_HelpKey].wasPressedThisFrame)
                m_ShowHelp = !m_ShowHelp;
            if (keyboard[m_CenterKey].wasPressedThisFrame)
                m_Gaze = Vector2.zero;
            if (keyboard[m_AmbientOcclusionKey].wasPressedThisFrame)
                m_ScreenSpaceAmbientOcclusion = !m_ScreenSpaceAmbientOcclusion;

            var move = Vector2.zero;
            if (keyboard.leftArrowKey.isPressed)
                move.x -= 1f;
            if (keyboard.rightArrowKey.isPressed)
                move.x += 1f;
            if (keyboard.downArrowKey.isPressed)
                move.y -= 1f;
            if (keyboard.upArrowKey.isPressed)
                move.y += 1f;
            if (move != Vector2.zero)
            {
                var speed = m_GazeSpeed * 2f * (keyboard.shiftKey.isPressed ? 3f : 1f);
                m_Gaze = Vector2.Max(-Vector2.one, Vector2.Min(Vector2.one, m_Gaze + move * speed * Time.unscaledDeltaTime));
            }
        }

        void FollowMouse()
        {
            var mouse = Mouse.current;
            if (mouse == null || mouse.rightButton.isPressed || Screen.width == 0 || Screen.height == 0)
                return;
            var position = mouse.position.ReadValue();
            m_Gaze = new Vector2(position.x / Screen.width * 2f - 1f, position.y / Screen.height * 2f - 1f);
            m_Gaze = Vector2.Max(-Vector2.one, Vector2.Min(Vector2.one, m_Gaze));
        }

        void MoveCamera()
        {
            var camera = Camera.main;
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (camera == null || mouse == null || keyboard == null)
                return;

            var looking = mouse.rightButton.isPressed;
            var moving = keyboard.wKey.isPressed || keyboard.aKey.isPressed || keyboard.sKey.isPressed || keyboard.dKey.isPressed ||
                keyboard.eKey.isPressed || keyboard.qKey.isPressed;
            if (!looking && !moving)
                return;

            // Without a headset the pose driver has nothing to track; switch it off so it cannot reset the camera.
            if (m_DisabledPoseDriver == null)
            {
                var driver = camera.GetComponent<TrackedPoseDriver>();
                if (driver != null && driver.enabled)
                {
                    driver.enabled = false;
                    m_DisabledPoseDriver = driver;
                }
            }
            if (!m_LookInitialized)
            {
                var euler = camera.transform.eulerAngles;
                m_Yaw = euler.y;
                m_Pitch = euler.x > 180f ? euler.x - 360f : euler.x;
                m_LookInitialized = true;
            }

            var transform = camera.transform;
            if (looking)
            {
                var delta = mouse.delta.ReadValue() * m_LookSensitivity;
                m_Yaw += delta.x;
                m_Pitch = Mathf.Clamp(m_Pitch - delta.y, -89f, 89f);
                transform.rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            }

            var direction = Vector3.zero;
            if (keyboard.wKey.isPressed)
                direction += transform.forward;
            if (keyboard.sKey.isPressed)
                direction -= transform.forward;
            if (keyboard.dKey.isPressed)
                direction += transform.right;
            if (keyboard.aKey.isPressed)
                direction -= transform.right;
            if (keyboard.eKey.isPressed)
                direction += Vector3.up;
            if (keyboard.qKey.isPressed)
                direction -= Vector3.up;
            var speed = m_MoveSpeed * (keyboard.shiftKey.isPressed ? 3f : 1f);
            transform.position += direction * speed * Time.unscaledDeltaTime;
        }

        void RestorePoseDriver()
        {
            if (m_DisabledPoseDriver != null)
                m_DisabledPoseDriver.enabled = true;
            m_DisabledPoseDriver = null;
            m_LookInitialized = false;
        }

        bool StartSimulation()
        {
            if (s_Running != null && s_Running != this)
                return false;

            m_Camera = Camera.main;
            if (m_Camera == null)
                return false;

            var focus = new GameObject("[Quad Views Simulator] Focus Camera");
            focus.hideFlags = HideFlags.DontSave;
            focus.transform.SetParent(m_Camera.transform, false);
            m_FocusCamera = focus.AddComponent<Camera>();
            var focusData = focus.AddComponent<UniversalAdditionalCameraData>();
            var mainData = m_Camera.GetComponent<UniversalAdditionalCameraData>();
            if (mainData != null)
            {
                focusData.renderPostProcessing = mainData.renderPostProcessing;
                focusData.antialiasing = mainData.antialiasing;
            }

            // Both views render to textures, so a camera that only clears keeps the screen defined behind the overlay.
            var clear = new GameObject("[Quad Views Simulator] Clear Camera");
            clear.hideFlags = HideFlags.DontSave;
            clear.transform.SetParent(transform, false);
            m_ClearCamera = clear.AddComponent<Camera>();
            m_ClearCamera.cullingMask = 0;
            m_ClearCamera.clearFlags = CameraClearFlags.SolidColor;
            m_ClearCamera.backgroundColor = Color.black;
            m_ClearCamera.depth = -100f;

            m_CanvasRoot = new GameObject("[Quad Views Simulator] Output");
            m_CanvasRoot.hideFlags = HideFlags.DontSave;
            m_CanvasRoot.transform.SetParent(transform, false);
            var canvas = m_CanvasRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -1000;
            m_Material = new Material(m_Shader) { hideFlags = HideFlags.DontSave };
            m_Image = new GameObject("Image", typeof(RectTransform)).AddComponent<RawImage>();
            m_Image.transform.SetParent(m_CanvasRoot.transform, false);
            m_Image.material = m_Material;
            m_Image.raycastTarget = false;
            var rect = m_Image.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            s_Running = this;
            return true;
        }

        void StopSimulation()
        {
            if (s_Running != this)
                return;

            if (m_Camera != null)
            {
                m_Camera.targetTexture = null;
                m_Camera.ResetProjectionMatrix();
            }
            if (m_FocusCamera != null)
            {
                m_FocusCamera.targetTexture = null;
                Destroy(m_FocusCamera.gameObject);
            }
            if (m_ClearCamera != null)
                Destroy(m_ClearCamera.gameObject);
            if (m_CanvasRoot != null)
                Destroy(m_CanvasRoot);
            if (m_Material != null)
                Destroy(m_Material);
            Release(ref m_PeripheryTarget);
            Release(ref m_FocusTarget);

            m_Camera = null;
            m_FocusCamera = null;
            m_ClearCamera = null;
            s_Running = null;
        }

        static void Release(ref RenderTexture target)
        {
            if (target == null)
                return;
            target.Release();
            Destroy(target);
            target = null;
        }

        static RenderTexture EnsureTarget(RenderTexture target, int width, int height, string name)
        {
            var msaa = UniversalRenderPipeline.asset != null ? Mathf.Max(1, UniversalRenderPipeline.asset.msaaSampleCount) : 1;
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            if (target != null && target.width == width && target.height == height && target.antiAliasing == msaa)
                return target;

            Release(ref target);
            target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                antiAliasing = msaa,
                name = name,
                hideFlags = HideFlags.DontSave,
            };
            target.Create();
            return target;
        }

        void UpdateViews()
        {
            if (m_Camera == null || Camera.main != m_Camera)
            {
                StopSimulation();
                return;
            }

            int width = Screen.width, height = Screen.height;
            if (width <= 0 || height <= 0)
                return;

            m_PeripheryTarget = EnsureTarget(m_PeripheryTarget, Mathf.RoundToInt(width * m_PeripheralMultiplier),
                Mathf.RoundToInt(height * m_PeripheralMultiplier), "[Quad Views Simulator] Peripheral View");
            m_Camera.targetTexture = m_PeripheryTarget;

            m_FocusRect = ComputeFocusRect(width, height, out var gazeUv);
            m_FocusTarget = EnsureTarget(m_FocusTarget, Mathf.RoundToInt(m_FocusRect.width * m_FocusMultiplier),
                Mathf.RoundToInt(m_FocusRect.height * m_FocusMultiplier), "[Quad Views Simulator] Focus View");

            m_FocusCamera.CopyFrom(m_Camera);
            m_FocusCamera.depth = m_Camera.depth - 1f;
            m_FocusCamera.targetTexture = m_FocusTarget;
            m_FocusCamera.projectionMatrix = FocusProjection(width, height, m_FocusRect);
            m_FocusCamera.transform.localPosition = Vector3.zero;
            m_FocusCamera.transform.localRotation = Quaternion.identity;

            m_Image.texture = m_PeripheryTarget;
            m_Material.SetTexture(k_FocusTex, m_FocusTarget);
            m_Material.SetVector(k_FocusRect, new Vector4((float)m_FocusRect.xMin / width, (float)m_FocusRect.yMin / height,
                (float)m_FocusRect.xMax / width, (float)m_FocusRect.yMax / height));
            m_Material.SetVector(k_GazeId, gazeUv);
            m_Material.SetFloat(k_Smoothing, m_SmoothenFocusViewEdges);
            m_Material.SetFloat(k_Sharpen, m_SharpenFocusView);
            m_Material.SetFloat(k_ShowRegion, m_ShowRegion ? 1f : 0f);
            m_Material.SetFloat(k_PeripheryTint, m_PeripheryTint);
            m_Material.SetVector(k_ScreenSize, new Vector4(width, height, 0f, 0f));
        }

        // SSAO is a renderer feature shared by every camera using the renderer, so its state is remembered and restored.
        void ApplyAmbientOcclusion()
        {
            ambientOcclusionEnabled = m_ScreenSpaceAmbientOcclusion;
            if (m_Renderer == null)
                return;
            foreach (var feature in m_Renderer.rendererFeatures)
            {
                if (feature == null || feature.GetType().Name != "ScreenSpaceAmbientOcclusion")
                    continue;
                if (!m_OriginalFeatureStates.ContainsKey(feature))
                    m_OriginalFeatureStates[feature] = feature.isActive;
                var active = m_OriginalFeatureStates[feature] && m_ScreenSpaceAmbientOcclusion;
                if (feature.isActive != active)
                    feature.SetActive(active);
            }
        }

        void RestoreRendererFeatures()
        {
            foreach (var pair in m_OriginalFeatureStates)
            {
                if (pair.Key != null && pair.Key.isActive != pair.Value)
                    pair.Key.SetActive(pair.Value);
            }
            m_OriginalFeatureStates.Clear();
        }

        // Focus rectangle in whole screen pixels. Its size is the section of the field of view; eye tracked mode
        // centers it on the gaze, fixed mode on the view center. It is kept inside the view.
        RectInt ComputeFocusRect(int width, int height, out Vector2 gazeUv)
        {
            var eyeTracked = m_Mode == QuadViewsSimulationMode.EyeTracked;
            var section = eyeTracked ? new Vector2(m_HorizontalFocusSection, m_VerticalFocusSection) : new Vector2(m_HorizontalFixedSection, m_VerticalFixedSection);
            var offset = eyeTracked ? m_VerticalFocusOffset : m_VerticalFixedOffset;
            var center = eyeTracked ? m_Gaze : Vector2.zero;
            center.y += offset * 2f;

            var tanFullY = Mathf.Tan(m_Camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var tanFullX = tanFullY * width / height;
            var tanFocusX = Mathf.Tan(Mathf.Atan(tanFullX) * section.x);
            var tanFocusY = Mathf.Tan(Mathf.Atan(tanFullY) * section.y);
            var focusWidth = Mathf.Clamp(Mathf.RoundToInt(width * tanFocusX / tanFullX), 1, width);
            var focusHeight = Mathf.Clamp(Mathf.RoundToInt(height * tanFocusY / tanFullY), 1, height);

            gazeUv = new Vector2(center.x * 0.5f + 0.5f, center.y * 0.5f + 0.5f);
            var x = Mathf.Clamp(Mathf.RoundToInt(gazeUv.x * width - focusWidth * 0.5f), 0, width - focusWidth);
            var y = Mathf.Clamp(Mathf.RoundToInt(gazeUv.y * height - focusHeight * 0.5f), 0, height - focusHeight);
            if (!eyeTracked)
                gazeUv = new Vector2(-1f, -1f);
            return new RectInt(x, y, focusWidth, focusHeight);
        }

        // Off-center projection covering exactly the focus rectangle's pixels of the main camera's view.
        Matrix4x4 FocusProjection(int width, int height, RectInt rect)
        {
            var near = m_Camera.nearClipPlane;
            var tanFullY = Mathf.Tan(m_Camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var tanFullX = tanFullY * width / height;
            var left = ((float)rect.xMin / width * 2f - 1f) * tanFullX;
            var right = ((float)rect.xMax / width * 2f - 1f) * tanFullX;
            var bottom = ((float)rect.yMin / height * 2f - 1f) * tanFullY;
            var top = ((float)rect.yMax / height * 2f - 1f) * tanFullY;
            return Matrix4x4.Frustum(left * near, right * near, bottom * near, top * near, near, m_Camera.farClipPlane);
        }

        void OnGUI()
        {
            if (!m_ShowHelp || IsXRRunning())
                return;

            var text = BuildHelp();
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, richText = true };
            var size = style.CalcSize(new GUIContent(text));
            GUI.Box(new Rect(Screen.width - size.x - 10, 10, size.x, size.y), text, style);
        }

        string BuildHelp()
        {
            var sb = new StringBuilder();
            var state = m_Mode == QuadViewsSimulationMode.Off ? "<color=#FF8C00>OFF</color>"
                : m_Mode == QuadViewsSimulationMode.Fixed ? "<color=#7CFC00>FIXED</color>" : "<color=#7CFC00>EYE TRACKED</color>";
            sb.AppendLine($"<b>Quad views simulation (no headset): {state}</b>");
            if (isSimulating)
            {
                var full = (double)Screen.width * Screen.height;
                var rendered = (double)m_PeripheryTarget.width * m_PeripheryTarget.height + (double)m_FocusTarget.width * m_FocusTarget.height;
                sb.AppendLine(string.Format(k_Inv, "Gaze {0:+0.00;-0.00} {1:+0.00;-0.00}   focus {2}x{3} px, periphery {4}x{5} px",
                    m_Gaze.x, m_Gaze.y, m_FocusTarget.width, m_FocusTarget.height, m_PeripheryTarget.width, m_PeripheryTarget.height));
                sb.AppendLine(string.Format(k_Inv, "Pixels {0:F2} M instead of {1:F2} M ({2:+0;-0}%)", rendered / 1e6, full / 1e6, (rendered / full - 1.0) * 100.0));
            }
            sb.AppendLine($"{m_ToggleKey}: on/off   {m_ModeKey}: fixed/eye tracked   {m_RegionKey}: show region   {m_CenterKey}: center gaze");
            sb.AppendLine($"Arrows: move gaze (Shift faster)   {m_AmbientOcclusionKey}: SSAO {(m_ScreenSpaceAmbientOcclusion ? "on" : "off")}   {m_HelpKey}: hide this");
            if (m_CameraControls)
                sb.Append("Right mouse: look   WASD: move   E/Q: up/down");
            return sb.ToString();
        }

        // The layer's settings.cfg keys and defaults, from the Advanced Configuration page of Quad-Views-Foveated.
        static readonly (string key, float defaultValue, float min, float max)[] k_LayerSettings =
        {
            ("peripheral_multiplier", 0.5f, 0.05f, 1f),
            ("focus_multiplier", 1f, 0.25f, 2f),
            ("horizontal_focus_section", 0.35f, 0.1f, 0.9f),
            ("vertical_focus_section", 0.35f, 0.1f, 0.9f),
            ("vertical_focus_offset", 0f, -0.5f, 0.5f),
            ("horizontal_fixed_section", 0.5f, 0.1f, 0.9f),
            ("vertical_fixed_section", 0.45f, 0.1f, 0.9f),
            ("vertical_fixed_offset", 0f, -0.5f, 0.5f),
            ("smoothen_focus_view_edges", 0.2f, 0f, 0.5f),
            ("sharpen_focus_view", 0.7f, 0f, 1f),
        };

        /// <summary>Developer settings of the installed layer, then the user's own file; later values win, like in the layer.</summary>
        public static readonly string[] layerSettingsFiles =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenXR-Quad-Views-Foveated", "settings.cfg"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quad-Views-Foveated", "settings.cfg"),
        };

        float GetLayerSetting(string key)
        {
            switch (key)
            {
                case "peripheral_multiplier": return m_PeripheralMultiplier;
                case "focus_multiplier": return m_FocusMultiplier;
                case "horizontal_focus_section": return m_HorizontalFocusSection;
                case "vertical_focus_section": return m_VerticalFocusSection;
                case "vertical_focus_offset": return m_VerticalFocusOffset;
                case "horizontal_fixed_section": return m_HorizontalFixedSection;
                case "vertical_fixed_section": return m_VerticalFixedSection;
                case "vertical_fixed_offset": return m_VerticalFixedOffset;
                case "smoothen_focus_view_edges": return m_SmoothenFocusViewEdges;
                case "sharpen_focus_view": return m_SharpenFocusView;
                default: throw new ArgumentException(key);
            }
        }

        void SetLayerSetting(string key, float value)
        {
            switch (key)
            {
                case "peripheral_multiplier": m_PeripheralMultiplier = value; break;
                case "focus_multiplier": m_FocusMultiplier = value; break;
                case "horizontal_focus_section": m_HorizontalFocusSection = value; break;
                case "vertical_focus_section": m_VerticalFocusSection = value; break;
                case "vertical_focus_offset": m_VerticalFocusOffset = value; break;
                case "horizontal_fixed_section": m_HorizontalFixedSection = value; break;
                case "vertical_fixed_section": m_VerticalFixedSection = value; break;
                case "vertical_fixed_offset": m_VerticalFixedOffset = value; break;
                case "smoothen_focus_view_edges": m_SmoothenFocusViewEdges = value; break;
                case "sharpen_focus_view": m_SharpenFocusView = value; break;
                default: throw new ArgumentException(key);
            }
        }

        /// <summary>Sets every layer setting to the layer's default and centers the gaze.</summary>
        [ContextMenu("Reset to Quad-Views-Foveated defaults")]
        public void ResetToLayerDefaults()
        {
            foreach (var setting in k_LayerSettings)
                SetLayerSetting(setting.key, setting.defaultValue);
            m_Gaze = Vector2.zero;
        }

        /// <summary>
        /// Applies the settings the installed layer would use on this computer: its defaults, then the developer and user
        /// settings.cfg files. Sections are applied like the layer does when they match: no section, the SteamVR runtime,
        /// [app:product name] and [exe:executable]. Returns false when no settings file exists.
        /// </summary>
        public bool LoadInstalledLayerSettings(out string report, string[] files = null)
        {
            files = files ?? layerSettingsFiles;
            var log = new StringBuilder();
            var values = new Dictionary<string, float>();
            foreach (var setting in k_LayerSettings)
                values[setting.key] = setting.defaultValue;

            var found = false;
            var exe = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";
            foreach (var file in files)
            {
                if (!File.Exists(file))
                {
                    log.AppendLine($"Not found: {file}");
                    continue;
                }

                found = true;
                log.AppendLine($"Read: {file}");
                var section = "";
                foreach (var raw in File.ReadAllLines(file))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                        continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }

                    var equals = line.IndexOf('=');
                    if (equals <= 0)
                        continue;
                    var key = line.Substring(0, equals).Trim().ToLowerInvariant();
                    var text = line.Substring(equals + 1).Trim();
                    var applies = section.Length == 0 ||
                        section.StartsWith("SteamVR", StringComparison.OrdinalIgnoreCase) ||
                        section.Equals("app:" + Application.productName, StringComparison.OrdinalIgnoreCase) ||
                        section.Equals("exe:" + exe, StringComparison.OrdinalIgnoreCase);
                    if (!values.ContainsKey(key))
                        continue;
                    if (!applies)
                    {
                        log.AppendLine($"  skipped [{section}] {key}={text}");
                        continue;
                    }
                    if (float.TryParse(text, NumberStyles.Float, k_Inv, out var value))
                    {
                        values[key] = value;
                        log.AppendLine($"  {(section.Length > 0 ? $"[{section}] " : "")}{key}={text}");
                    }
                }
            }

            if (found)
            {
                foreach (var setting in k_LayerSettings)
                    SetLayerSetting(setting.key, Mathf.Clamp(values[setting.key], setting.min, setting.max));
                m_Gaze = Vector2.zero;
            }
            report = log.ToString();
            return found;
        }

        /// <summary>The settings in the layer's settings.cfg format, to use the same values in the headset.</summary>
        [ContextMenu("Copy settings as Quad-Views-Foveated settings.cfg")]
        public void CopyLayerSettings()
        {
            var cfg = new StringBuilder();
            foreach (var setting in k_LayerSettings)
                cfg.AppendLine($"{setting.key}={GetLayerSetting(setting.key).ToString("0.###", k_Inv)}");
            GUIUtility.systemCopyBuffer = cfg.ToString();
            Debug.Log("[QuadViews] Copied to the clipboard. Paste into %LocalAppData%\\Quad-Views-Foveated\\settings.cfg; " +
                "the layer reads it when the XR session starts (press 1 in the headset to restart it).\n" + cfg);
        }
    }
}
