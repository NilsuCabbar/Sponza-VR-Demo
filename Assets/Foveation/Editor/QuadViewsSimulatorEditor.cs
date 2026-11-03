using System.IO;
using UnityEditor;
using UnityEngine;

namespace Foveation.Editor
{
    /// <summary>
    /// Inspector for QuadViewsSimulator: buttons that set every quad views setting at once to match the
    /// Quad-Views-Foveated layer, in Edit Mode and at runtime.
    /// </summary>
    [CustomEditor(typeof(QuadViewsSimulator))]
    public class QuadViewsSimulatorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var simulator = (QuadViewsSimulator)target;

            EditorGUILayout.LabelField("Match the headset", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Sets all resolution, focus region, blending and sharpening settings at once. " +
                "The mode, SSAO, gaze controls and keys stay as they are.", MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Quad-Views-Foveated defaults",
                        "The layer's own default values, as the headset uses them without a settings.cfg.")))
                {
                    Undo.RecordObject(simulator, "Reset to Quad-Views-Foveated defaults");
                    simulator.ResetToLayerDefaults();
                    EditorUtility.SetDirty(simulator);
                    Debug.Log("[QuadViews] Simulator set to the Quad-Views-Foveated defaults.");
                }

                var installed = System.Array.Exists(QuadViewsSimulator.layerSettingsFiles, File.Exists);
                using (new EditorGUI.DisabledScope(!installed))
                {
                    if (GUILayout.Button(new GUIContent("Load installed layer settings",
                            installed
                                ? "The values the installed layer uses on this computer: its defaults plus the developer and user settings.cfg."
                                : "The layer is not installed on this computer (no settings.cfg found).")))
                    {
                        Undo.RecordObject(simulator, "Load Quad-Views-Foveated settings");
                        if (simulator.LoadInstalledLayerSettings(out var report))
                        {
                            EditorUtility.SetDirty(simulator);
                            Debug.Log("[QuadViews] Simulator set to the installed layer's settings.\n" + report);
                        }
                        else
                        {
                            Debug.LogWarning("[QuadViews] No Quad-Views-Foveated settings.cfg found.\n" + report);
                        }
                    }
                }
            }

            if (GUILayout.Button(new GUIContent("Copy as settings.cfg",
                    "Copies the current values in the layer's format, to paste into %LocalAppData%\\Quad-Views-Foveated\\settings.cfg.")))
                simulator.CopyLayerSettings();

            EditorGUILayout.Space();
            DrawDefaultInspector();
        }
    }
}
