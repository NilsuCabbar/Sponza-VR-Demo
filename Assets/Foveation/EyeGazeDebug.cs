using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace Foveation
{
    /// <summary>
    /// Checks in the headset whether eye gaze reaches Unity through OpenXR (XR_EXT_eye_gaze_interaction), and shows
    /// where the eyes look with a small dot in front of them. Logs every change of the tracking state to the Console;
    /// PerformanceOverlay shows <see cref="status"/>.
    ///
    /// The Quad-Views-Foveated layer reads the gaze on its own; this only tells whether the runtime delivers it.
    /// </summary>
    public class EyeGazeDebug : MonoBehaviour
    {
        public enum GazeState { NoDevice, NotTracked, Tracked }

        /// <summary>Current gaze state, for the performance overlay.</summary>
        public static GazeState state { get; private set; } = GazeState.NoDevice;

        /// <summary>Gaze direction relative to the head: x right, y up, in degrees. Valid while tracked.</summary>
        public static Vector2 angles { get; private set; }

        public static string status
        {
            get
            {
                switch (state)
                {
                    case GazeState.Tracked: return $"<color=#7CFC00>Eye gaze tracked</color>  {angles.x:+0;-0}° {angles.y:+0;-0}°";
                    case GazeState.NotTracked: return "<color=#FF8C00>Eye gaze device found, NOT tracked</color>";
                    default: return "<color=#FF8C00>Eye gaze: no device</color>";
                }
            }
        }

        [Tooltip("Key that shows or hides the gaze dot.")]
        [SerializeField]
        Key m_ToggleKey = Key.G;

        [SerializeField]
        bool m_ShowDot = true;

        [Tooltip("Distance of the gaze dot from the eyes, in meters.")]
        [SerializeField]
        float m_DotDistance = 1.5f;

        [Tooltip("Diameter of the gaze dot, in meters.")]
        [SerializeField]
        float m_DotSize = 0.015f;

        [SerializeField]
        Color m_DotColor = new Color(1f, 0.15f, 0.15f);

        // Referenced so builds include the shader.
        [SerializeField, HideInInspector]
        Shader m_DotShader;

        GameObject m_Dot;

        void OnValidate()
        {
            if (m_DotShader == null)
                m_DotShader = Shader.Find("Universal Render Pipeline/Unlit");
        }
        GazeState m_LoggedState = (GazeState)(-1);

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard[m_ToggleKey].wasPressedThisFrame)
                m_ShowDot = !m_ShowDot;

            var device = InputSystem.GetDevice<EyeGazeInteraction.EyeGazeDevice>();
            if (device == null)
                state = GazeState.NoDevice;
            else
                state = device.pose.isTracked.ReadValue() > 0.5f ? GazeState.Tracked : GazeState.NotTracked;

            if (state != m_LoggedState)
            {
                Debug.Log($"[EyeGaze] {state}");
                m_LoggedState = state;
            }

            var camera = Camera.main;
            var visible = m_ShowDot && state == GazeState.Tracked && camera != null;
            if (!visible)
            {
                if (m_Dot != null)
                    m_Dot.SetActive(false);
                return;
            }

            // The gaze pose is in tracking space, like the head pose that drives the camera's local transform.
            var space = camera.transform.parent;
            var position = device.pose.position.ReadValue();
            var rotation = device.pose.rotation.ReadValue();
            var worldPosition = space != null ? space.TransformPoint(position) : position;
            var worldDirection = (space != null ? space.rotation : Quaternion.identity) * rotation * Vector3.forward;

            var local = camera.transform.InverseTransformDirection(worldDirection);
            angles = new Vector2(Mathf.Atan2(local.x, local.z), Mathf.Atan2(local.y, local.z)) * Mathf.Rad2Deg;

            if (m_Dot == null)
                CreateDot();
            m_Dot.SetActive(true);
            m_Dot.transform.position = worldPosition + worldDirection * m_DotDistance;
            m_Dot.transform.localScale = Vector3.one * m_DotSize;
        }

        void CreateDot()
        {
            m_Dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            m_Dot.name = "[Eye Gaze Dot]";
            m_Dot.hideFlags = HideFlags.DontSave;
            Destroy(m_Dot.GetComponent<Collider>());
            var renderer = m_Dot.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var material = new Material(m_DotShader != null ? m_DotShader : Shader.Find("Universal Render Pipeline/Unlit")) { color = m_DotColor };
            renderer.material = material;
            m_Dot.layer = LayerMask.NameToLayer("UI");
        }

        void OnDisable()
        {
            if (m_Dot != null)
                Destroy(m_Dot);
            state = GazeState.NoDevice;
        }
    }
}
