using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Foveation;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR;

namespace Benchmark
{
    /// <summary>
    /// Live performance overlay that stays in view inside the headset (and on the desktop mirror), like the FPS
    /// overlays used in game benchmark videos. Shows frame/CPU/GPU time, a frame time graph, triangles, draw calls,
    /// memory, the XR views Unity renders and their pixel count, and whether quad views is active.
    /// A recording writes every frame to BenchmarkResults/session_&lt;date&gt;.csv and summarizes it per quad views state.
    /// </summary>
    public class PerformanceOverlay : MonoBehaviour
    {
        [SerializeField]
        Key m_ToggleKey = Key.F1;

        [SerializeField]
        Key m_RecordKey = Key.F2;

        [SerializeField]
        bool m_Visible = true;

        [Tooltip("Distance of the overlay in front of the eyes, in meters.")]
        [SerializeField]
        float m_Distance = 1.5f;

        [Tooltip("Offset from the view center in meters (x right, y up). Keep it inside the focus region to read it sharply with quad views.")]
        [SerializeField]
        Vector2 m_Offset = new Vector2(-0.25f, 0.1f);

        [Tooltip("Text refresh interval in seconds. The values are averaged over this interval.")]
        [SerializeField]
        float m_RefreshInterval = 0.25f;

        const int k_GraphWidth = 240;
        const int k_GraphHeight = 64;
        const float k_GraphMaxMs = 25f;

        struct Sample
        {
            public float time, frameMs, cpuMs, gpuMs, mainMs, renderMs;
            public long triangles, vertices, drawCalls, setPassCalls;
            public bool quadViews;
            public int views;
            public long pixels;
        }

        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;

        Camera m_Camera;
        GameObject m_Root;
        Text m_Text;
        Texture2D m_Graph;
        Color32[] m_GraphPixels;
        readonly Queue<float> m_FrameHistory = new Queue<float>();
        readonly Queue<float> m_GpuHistory = new Queue<float>();

        ProfilerRecorder m_Triangles, m_Vertices, m_DrawCalls, m_SetPass, m_TextureMemory, m_GfxMemory;
        readonly FrameTiming[] m_Timings = new FrameTiming[1];
        readonly List<Sample> m_Window = new List<Sample>();
        float m_NextRefresh;
        float m_LastGpuMs, m_LastCpuMs, m_LastMainMs, m_LastRenderMs;

        List<Sample> m_Recording;
        float m_RecordingStart;

        void OnEnable()
        {
            m_Triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            m_Vertices = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
            m_DrawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            m_SetPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            m_TextureMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Texture Memory");
            m_GfxMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Gfx Used Memory");
        }

        void OnDisable()
        {
            m_Triangles.Dispose();
            m_Vertices.Dispose();
            m_DrawCalls.Dispose();
            m_SetPass.Dispose();
            m_TextureMemory.Dispose();
            m_GfxMemory.Dispose();
            if (m_Recording != null)
                StopRecording();
            if (m_Root != null)
                Destroy(m_Root);
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard[m_ToggleKey].wasPressedThisFrame)
                    m_Visible = !m_Visible;
                if (keyboard[m_RecordKey].wasPressedThisFrame)
                {
                    if (m_Recording == null)
                        StartRecording();
                    else
                        StopRecording();
                }
            }

            var sample = TakeSample();
            m_Window.Add(sample);
            m_Recording?.Add(sample);
            Push(m_FrameHistory, sample.frameMs);
            Push(m_GpuHistory, sample.gpuMs);

            AttachToCamera();
            if (m_Root != null)
                m_Root.SetActive(m_Visible);

            if (Time.unscaledTime >= m_NextRefresh && m_Window.Count > 0)
            {
                m_NextRefresh = Time.unscaledTime + m_RefreshInterval;
                if (m_Visible && m_Text != null)
                {
                    m_Text.text = BuildText();
                    DrawGraph();
                }
                m_Window.Clear();
            }
        }

        Sample TakeSample()
        {
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, m_Timings) > 0)
            {
                // Timings arrive a few frames late; keep the last valid value for the frames in between.
                if (m_Timings[0].gpuFrameTime > 0)
                    m_LastGpuMs = (float)m_Timings[0].gpuFrameTime;
                if (m_Timings[0].cpuFrameTime > 0)
                    m_LastCpuMs = (float)m_Timings[0].cpuFrameTime;
                if (m_Timings[0].cpuMainThreadFrameTime > 0)
                    m_LastMainMs = (float)m_Timings[0].cpuMainThreadFrameTime;
                if (m_Timings[0].cpuRenderThreadFrameTime > 0)
                    m_LastRenderMs = (float)m_Timings[0].cpuRenderThreadFrameTime;
            }

            GetXRViews(out var views, out var pixels);
            return new Sample
            {
                time = Time.unscaledTime,
                frameMs = Time.unscaledDeltaTime * 1000f,
                cpuMs = m_LastCpuMs,
                gpuMs = m_LastGpuMs,
                mainMs = m_LastMainMs,
                renderMs = m_LastRenderMs,
                triangles = m_Triangles.Valid ? m_Triangles.LastValue : -1,
                vertices = m_Vertices.Valid ? m_Vertices.LastValue : -1,
                drawCalls = m_DrawCalls.Valid ? m_DrawCalls.LastValue : -1,
                setPassCalls = m_SetPass.Valid ? m_SetPass.LastValue : -1,
                quadViews = QuadViewsFoveationFeature.isQuadViewsEnabled,
                views = views,
                pixels = pixels,
            };
        }

        // Views and pixels Unity renders for the headset: 2 views with stereo, 4 with quad views.
        static void GetXRViews(out int views, out long pixels)
        {
            views = 0;
            pixels = 0;
            var display = GetDisplay();
            if (display == null || !display.running)
                return;

            for (var i = 0; i < display.GetRenderPassCount(); i++)
            {
                display.GetRenderPass(i, out var pass);
                var count = pass.GetRenderParameterCount();
                views += count;
                pixels += (long)pass.renderTargetScaledWidth * pass.renderTargetScaledHeight * count;
            }
        }

        static XRDisplaySubsystem GetDisplay()
        {
            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            return displays.FirstOrDefault(d => d.running);
        }

        string BuildText()
        {
            var w = m_Window;
            var frame = w.Average(s => s.frameMs);
            var worst = w.Max(s => s.frameMs);
            var sb = new StringBuilder();
            sb.AppendLine($"<b>{1000f / frame:F0} FPS</b>   frame {frame:F2} ms (max {worst:F1})");
            sb.AppendLine($"GPU {Avg(w, s => s.gpuMs)}   CPU {Avg(w, s => s.cpuMs)}");
            sb.AppendLine($"main {Avg(w, s => s.mainMs)}   render {Avg(w, s => s.renderMs)}");

            var display = GetDisplay();
            if (display != null && display.TryGetDisplayRefreshRate(out var hz) && hz > 0)
                sb.AppendLine($"Headset {hz:F0} Hz, budget {1000f / hz:F1} ms");

            var last = w[w.Count - 1];
            sb.AppendLine($"Triangles {Count(last.triangles)}   Vertices {Count(last.vertices)}");
            sb.AppendLine($"Draw calls {Count(last.drawCalls)}   SetPass {Count(last.setPassCalls)}");
            sb.AppendLine($"VRAM: textures {Mb(m_TextureMemory)}   total {Mb(m_GfxMemory)}");

            if (last.views > 0)
                sb.AppendLine($"XR: {last.views} views, {last.pixels / 1e6:F2} M pixels");
            else
                sb.AppendLine("XR: not running (desktop)");
            if (last.views > 0)
                sb.AppendLine(EyeGazeDebug.status);
            if (QuadViewsSimulator.isSimulating && last.views == 0)
                sb.AppendLine("<color=#7CFC00>Quad views SIMULATED (desktop)</color>");
            else
                sb.AppendLine(last.quadViews ? "<color=#7CFC00>Quad views ON</color>" : "<color=#FF8C00>Quad views OFF</color>");
            // Quad views settings: in the headset the layer's (settings.cfg), on the desktop the simulation's.
            if (last.views > 0 && last.quadViews)
                sb.AppendLine(string.Format(k_Inv, "Focus {0:0}% x {1:0}% of view   periphery {2:0}% res{3}",
                    QuadViewsToggle.focusSection.x * 100f, QuadViewsToggle.focusSection.y * 100f, QuadViewsToggle.peripheralMultiplier * 100f,
                    QuadViewsToggle.applying ? "   <color=#FFD700>applying, XR restarts…</color>" : ""));
            else if (last.views == 0 && QuadViewsSimulator.isSimulating)
                sb.AppendLine(string.Format(k_Inv, "Focus {0:0}% x {1:0}% of view   periphery {2:0}% res",
                    QuadViewsSimulator.focusSection.x * 100f, QuadViewsSimulator.focusSection.y * 100f, QuadViewsSimulator.peripheralMultiplier * 100f));

            // Like the "Overlay: on/off" line of Sponza_Intel_Foveated_Rendering_Shader, with the colors' meaning below.
            sb.AppendLine($"Resolution colors: {(QuadViewsResolution.show ? "<color=#7CFC00>ON</color>" : "off")}  (R)");
            if (QuadViewsResolution.show)
            {
                var legend = QuadViewsResolution.Legend(Camera.main);
                sb.AppendLine(!string.IsNullOrEmpty(legend) ? legend
                    : QuadViewsResolution.LegendLine(QuadViewsResolution.ColorForRatio(1f), "whole screen", Screen.width, Screen.height, 1f));
            }

            if (m_Recording != null)
                sb.AppendLine($"<color=#FF4040>● REC {Time.unscaledTime - m_RecordingStart:F0} s</color>  ({m_RecordKey} to stop)");

            // Every key that does something in the headset (the desktop simulation has the same ones plus its own help panel).
            sb.AppendLine("<size=17><color=#BBBBBB>" +
                "1: quad views on/off (XR restarts)   R: resolution colors\n" +
                "PgUp/PgDn: focus region larger/smaller\n" +
                "Home/End: periphery resolution up/down   Backspace: reset\n" +
                $"{m_RecordKey}: record   {m_ToggleKey}: hide this panel" +
                "</color></size>");
            return sb.ToString();
        }

        static string Avg(List<Sample> window, Func<Sample, float> value)
        {
            var valid = window.Select(value).Where(v => v > 0).ToList();
            return valid.Count > 0 ? $"{valid.Average():F2} ms" : "–";
        }

        static string Count(long value) => value < 0 ? "–" : value.ToString("N0", k_Inv);

        static string Mb(ProfilerRecorder recorder) => recorder.Valid ? $"{recorder.LastValue / (1024f * 1024f):F0} MB" : "–";

        static void Push(Queue<float> queue, float value)
        {
            queue.Enqueue(value);
            while (queue.Count > k_GraphWidth)
                queue.Dequeue();
        }

        // Frame time (white) and GPU time (green) over the last frames; the yellow line is the headset's frame budget.
        void DrawGraph()
        {
            var background = new Color32(0, 0, 0, 0);
            for (var i = 0; i < m_GraphPixels.Length; i++)
                m_GraphPixels[i] = background;

            var budget = 1000f / 90f;
            var display = GetDisplay();
            if (display != null && display.TryGetDisplayRefreshRate(out var hz) && hz > 0)
                budget = 1000f / hz;
            var budgetY = Mathf.Clamp((int)(budget / k_GraphMaxMs * k_GraphHeight), 0, k_GraphHeight - 1);
            for (var x = 0; x < k_GraphWidth; x++)
                m_GraphPixels[budgetY * k_GraphWidth + x] = new Color32(255, 220, 0, 255);

            PlotColumn(m_FrameHistory, new Color32(255, 255, 255, 255));
            PlotColumn(m_GpuHistory, new Color32(80, 255, 80, 255));
            m_Graph.SetPixels32(m_GraphPixels);
            m_Graph.Apply(false);
        }

        void PlotColumn(Queue<float> history, Color32 color)
        {
            var x = k_GraphWidth - history.Count;
            foreach (var ms in history)
            {
                if (ms > 0)
                {
                    var y = Mathf.Clamp((int)(ms / k_GraphMaxMs * k_GraphHeight), 0, k_GraphHeight - 1);
                    m_GraphPixels[y * k_GraphWidth + x] = color;
                }
                x++;
            }
        }

        // Head-locked world space canvas under the active camera, drawn on top of the scene.
        void AttachToCamera()
        {
            var camera = Camera.main;
            if (camera == null || camera == m_Camera)
                return;

            m_Camera = camera;
            if (m_Root == null)
                BuildCanvas();
            m_Root.transform.SetParent(camera.transform, false);
            m_Root.transform.localPosition = new Vector3(m_Offset.x, m_Offset.y, m_Distance);
            m_Root.transform.localRotation = Quaternion.identity;
        }

        void BuildCanvas()
        {
            m_Root = new GameObject("[Performance Overlay]");
            var canvas = m_Root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 1000;
            var rect = (RectTransform)m_Root.transform;
            rect.sizeDelta = new Vector2(520, 600); // room for the resolution colors legend and the key list
            rect.localScale = Vector3.one * 0.0012f;

            // Draw over the scene so walls do not hide the overlay.
            var material = new Material(Shader.Find("UI/Default"));
            material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);

            var panel = new GameObject("Panel", typeof(RectTransform)).AddComponent<Image>();
            panel.transform.SetParent(m_Root.transform, false);
            panel.color = new Color(0f, 0f, 0f, 0.6f);
            panel.material = material;
            Stretch(panel.rectTransform);

            m_Text = new GameObject("Text", typeof(RectTransform)).AddComponent<Text>();
            m_Text.transform.SetParent(m_Root.transform, false);
            m_Text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_Text.fontSize = 22;
            m_Text.supportRichText = true;
            m_Text.color = Color.white;
            m_Text.material = material;
            Stretch(m_Text.rectTransform);
            m_Text.rectTransform.offsetMin = new Vector2(14, 90);
            m_Text.rectTransform.offsetMax = new Vector2(-14, -10);

            m_Graph = new Texture2D(k_GraphWidth, k_GraphHeight, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            m_GraphPixels = new Color32[k_GraphWidth * k_GraphHeight];
            var graph = new GameObject("Graph", typeof(RectTransform)).AddComponent<RawImage>();
            graph.transform.SetParent(m_Root.transform, false);
            graph.texture = m_Graph;
            graph.material = material;
            graph.rectTransform.anchorMin = new Vector2(0, 0);
            graph.rectTransform.anchorMax = new Vector2(1, 0);
            graph.rectTransform.pivot = new Vector2(0.5f, 0);
            graph.rectTransform.offsetMin = new Vector2(14, 10);
            graph.rectTransform.offsetMax = new Vector2(-14, 80);

            // UI layer, which the benchmark camera leaves out of its images.
            var layer = LayerMask.NameToLayer("UI");
            foreach (var t in m_Root.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = layer;
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        void StartRecording()
        {
            m_Recording = new List<Sample>();
            m_RecordingStart = Time.unscaledTime;
            Debug.Log("[Overlay] Recording started.");
        }

        void StopRecording()
        {
            var samples = m_Recording;
            m_Recording = null;
            if (samples.Count == 0)
                return;

            var folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BenchmarkResults"));
            Directory.CreateDirectory(folder);
            var name = "session_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", k_Inv);

            var csv = new StringBuilder("time_s,frame_ms,cpu_ms,gpu_ms,main_thread_ms,render_thread_ms,triangles,vertices,draw_calls,setpass_calls,quad_views,xr_views,xr_pixels\n");
            foreach (var s in samples)
            {
                csv.AppendLine(string.Join(",", F(s.time - samples[0].time), F(s.frameMs), F(s.cpuMs), F(s.gpuMs), F(s.mainMs), F(s.renderMs),
                    s.triangles.ToString(k_Inv), s.vertices.ToString(k_Inv), s.drawCalls.ToString(k_Inv), s.setPassCalls.ToString(k_Inv),
                    s.quadViews ? "1" : "0", s.views.ToString(k_Inv), s.pixels.ToString(k_Inv)));
            }
            File.WriteAllText(Path.Combine(folder, name + ".csv"), csv.ToString());

            // Summary per quad views state, so an on/off comparison is one recording.
            var md = new StringBuilder($"# {name}\n\n- {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}), {(Application.isEditor ? "Editor" : "Player")}\n\n");
            md.AppendLine("| Quad views | Frames | Frame ms | Frame p99 ms | GPU ms | GPU p99 ms | CPU ms | Triangles | Draw calls | XR views | XR M pixels |");
            md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var group in samples.GroupBy(s => s.quadViews))
            {
                var g = group.ToList();
                md.AppendLine(FormattableString.Invariant(
                    $"| {(group.Key ? "ON" : "OFF")} | {g.Count} | {g.Average(s => s.frameMs):F2} | {P99(g.Select(s => s.frameMs)):F2} | {g.Average(s => s.gpuMs):F2} | {P99(g.Select(s => s.gpuMs)):F2} | {g.Average(s => s.cpuMs):F2} | {g.Average(s => s.triangles):N0} | {g.Average(s => s.drawCalls):F0} | {g.Average(s => s.views):F0} | {g.Average(s => s.pixels) / 1e6:F2} |"));
            }
            File.WriteAllText(Path.Combine(folder, name + ".md"), md.ToString());
            Debug.Log($"[Overlay] Recording saved: {Path.Combine(folder, name)}.csv\n{md}");
        }

        static float P99(IEnumerable<float> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(0.99 * sorted.Length) - 1)];
        }

        static string F(float value) => value.ToString("0.###", k_Inv);
    }
}
