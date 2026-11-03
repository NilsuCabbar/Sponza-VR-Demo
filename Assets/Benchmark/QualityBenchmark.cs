using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Profiling;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Benchmark
{
    [Serializable]
    public class BenchmarkView
    {
        public string name;
        public Vector3 position;
        public Vector3 eulerAngles;

        public BenchmarkView(string name, Vector3 position, Vector3 eulerAngles)
        {
            this.name = name;
            this.position = position;
            this.eulerAngles = eulerAngles;
        }
    }

    public enum Override { Keep, On, Off }

    /// <summary>One set of render settings. Every value left at its "keep" default uses the quality level's own value.</summary>
    [Serializable]
    public class BenchmarkScenario
    {
        public string name;

        [Tooltip("URP asset whose settings are used, e.g. Mobile_RPAsset. Empty uses the active quality level's asset. " +
            "Quality levels are not switched, because in Play Mode and builds only the levels enabled for the platform exist.")]
        public UniversalRenderPipelineAsset pipeline;

        // Quality level whose URP asset Reset() puts into pipeline when the component is added.
        [NonSerialized]
        internal string defaultLevel;

        [Tooltip("URP render scale (0.1–2). 0 keeps the asset value.")]
        public float renderScale;

        [Tooltip("MSAA sample count: 1, 2, 4 or 8. 0 keeps the asset value.")]
        public int msaa;

        [Tooltip("Main light shadow map resolution (256–8192). 0 keeps the asset value.")]
        public int shadowResolution;

        [Tooltip("Shadow distance in meters. 0 turns realtime shadows off, negative keeps the asset value.")]
        public float shadowDistance = -1f;

        [Tooltip("Global texture mipmap limit: 0 full, 1 half, 2 quarter resolution. -1 keeps the level value.")]
        public int textureMipLimit = -1;

        public Override hdr = Override.Keep;

        [Tooltip("Render like the Quad-Views-Foveated layer: a reduced resolution view of the whole field of view plus a " +
            "full resolution focus view in the center, blended together. The center is fixed, like fixed foveation without eye tracking.")]
        public bool quadViews;

        [Tooltip("Resolution multiplier of the peripheral view per axis (layer setting peripheral_multiplier, default 0.5).")]
        public float peripheralMultiplier = 0.5f;

        [Tooltip("Resolution multiplier of the focus view per axis (layer setting focus_multiplier, default 1.0).")]
        public float focusMultiplier = 1f;

        [Tooltip("Focus region as a fraction of the field of view, x horizontal and y vertical. Layer defaults: " +
            "0.5 x 0.45 for fixed foveation, 0.35 x 0.35 with eye tracking.")]
        public Vector2 focusSection = new Vector2(0.5f, 0.45f);

        [Tooltip("Width of the blend between the focus and peripheral views, as a fraction of the focus region " +
            "(layer setting smoothen_focus_view_edges, default 0.2).")]
        [Range(0f, 0.5f)]
        public float focusEdgeSmoothing = 0.2f;

        public BenchmarkScenario() { }

        public BenchmarkScenario(string name) => this.name = name;
    }

    /// <summary>
    /// Renders fixed viewpoints under several render settings and records, per scenario and view:
    /// CPU/GPU frame time (FrameTimingManager), triangles, vertices, draw calls, batches, SetPass calls and memory
    /// (ProfilerRecorder), plus SSIM and PSNR against a reference image. Results go to BenchmarkResults/&lt;date&gt;/.
    /// </summary>
    public class QualityBenchmark : MonoBehaviour
    {
        public const string autoRunSessionKey = "Benchmark.QualityBenchmark.AutoRun";

        [Header("Run")]
        [Tooltip("Key that starts the benchmark in Play Mode.")]
        [SerializeField]
        Key m_RunKey = Key.B;

        [SerializeField]
        bool m_RunOnStart;

        [Header("Capture")]
        [Tooltip("Defaults match one eye of the HTC Vive Pro (Eye): 1440x1600, 110° diagonal field of view.")]
        [SerializeField]
        int m_Width = 1440;

        [SerializeField]
        int m_Height = 1600;

        [Tooltip("Vertical field of view in degrees. 93° with 1440x1600 gives the Vive Pro's 110° diagonal.")]
        [SerializeField]
        float m_FieldOfView = 93f;

        [Tooltip("Frames rendered before measuring each view, so shaders, textures and shadows have settled.")]
        [SerializeField]
        int m_WarmupFrames = 60;

        [Tooltip("Frames measured per view.")]
        [SerializeField]
        int m_MeasureFrames = 240;

        [SerializeField]
        bool m_SaveImages = true;

        [Header("Comparison")]
        [Tooltip("SSIM and PSNR compare every scenario against this one. Give it the highest quality settings.")]
        [SerializeField]
        int m_ReferenceScenario;

        [Tooltip("Frame time changes in the summary are relative to this scenario.")]
        [SerializeField]
        int m_BaselineScenario = 1;

        [Tooltip("Region used to split SSIM into focus, periphery and the seam between them, as a fraction of the field " +
            "of view. Use the same value as the quad views scenarios' focus section.")]
        [SerializeField]
        Vector2 m_MetricFocusSection = new Vector2(0.5f, 0.45f);

        [Tooltip("Width in pixels of the band around the focus region border that counts as the seam.")]
        [SerializeField]
        int m_SeamWidth = 24;

        [Tooltip("Optional: folder name of an earlier run in BenchmarkResults. Its reference images are used instead of " +
            "this run's reference scenario, e.g. to measure texture compression, which needs a reimport between runs.")]
        [SerializeField]
        string m_ExternalReferenceRun = "";

        [SerializeField]
        List<BenchmarkView> m_Views = new List<BenchmarkView>
        {
            new BenchmarkView("atrium", new Vector3(-9.5f, 1.7f, 0.5f), new Vector3(0f, 90f, 0f)),
            new BenchmarkView("atrium_back", new Vector3(9.5f, 1.7f, 0.5f), new Vector3(0f, -90f, 0f)),
            new BenchmarkView("colonnade", new Vector3(-9f, 1.7f, -4f), new Vector3(0f, 90f, 0f)),
            new BenchmarkView("upper_gallery", new Vector3(-8f, 6.3f, 4f), new Vector3(10f, 100f, 0f)),
            new BenchmarkView("curtains_plants", new Vector3(2f, 1.6f, 1f), new Vector3(5f, 20f, 0f)),
        };

        [SerializeField]
        List<BenchmarkScenario> m_Scenarios = new List<BenchmarkScenario>
        {
            new BenchmarkScenario("Reference (PC, 2x SSAA, MSAA 4x, 4K shadows)") { renderScale = 2f, msaa = 4, shadowResolution = 4096 },
            new BenchmarkScenario("PC (High)"),
            new BenchmarkScenario("Mobile (Low)") { defaultLevel = "Mobile" },
            new BenchmarkScenario("PC + render scale 0.75") { renderScale = 0.75f },
            new BenchmarkScenario("PC + render scale 0.5") { renderScale = 0.5f },
            new BenchmarkScenario("PC + MSAA 4x") { msaa = 4 },
            new BenchmarkScenario("PC + textures 1/2") { textureMipLimit = 1 },
            new BenchmarkScenario("PC + textures 1/4") { textureMipLimit = 2 },
            new BenchmarkScenario("PC + shadow map 512") { shadowResolution = 512 },
            new BenchmarkScenario("PC + no realtime shadows") { shadowDistance = 0f },
            new BenchmarkScenario("PC + HDR off") { hdr = Override.Off },
            new BenchmarkScenario("Quad views (fixed 0.5 x 0.45, periphery 0.5)") { quadViews = true },
            new BenchmarkScenario("Quad views (eye-tracked size 0.35 x 0.35)") { quadViews = true, focusSection = new Vector2(0.35f, 0.35f) },
            new BenchmarkScenario("Quad views, periphery 0.25") { quadViews = true, peripheralMultiplier = 0.25f },
            new BenchmarkScenario("Quad views, no edge smoothing") { quadViews = true, focusEdgeSmoothing = 0f },
        };

        struct Row
        {
            public string scenario, view, pipeline;
            public float renderScale, shadowDistance;
            public int msaa, shadowResolution, textureMipLimit, frames;
            public bool hdr, quadViews;
            public double renderedMegapixels;
            public double ssimFocus, ssimPeriphery, ssimSeam;
            public Stats cpu, gpu, mainThread, renderThread, frame;
            public double triangles, vertices, drawCalls, batches, setPassCalls, textureMemoryMB, gfxMemoryMB;
            public double ssim, psnr;
        }

        struct Stats
        {
            public double mean, median, p95, p99;

            public static Stats From(List<double> values)
            {
                if (values.Count == 0)
                    return new Stats { mean = double.NaN, median = double.NaN, p95 = double.NaN, p99 = double.NaN };

                var sorted = values.OrderBy(v => v).ToArray();
                return new Stats
                {
                    mean = sorted.Average(),
                    median = Percentile(sorted, 0.5),
                    p95 = Percentile(sorted, 0.95),
                    p99 = Percentile(sorted, 0.99),
                };
            }

            static double Percentile(double[] sorted, double p) =>
                sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
        }

        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;

        bool m_Running;
        bool m_ExitPlayModeWhenDone;
        string m_Status = "";
        Camera m_Camera;
        RenderTexture m_Target;
        Camera m_FocusCamera;
        RenderTexture m_FocusTarget;
        BenchmarkScenario m_Current;
        RectInt m_FocusRect;
        UniversalRenderPipelineAsset m_PipelineClone;
        readonly List<Camera> m_DisabledCameras = new List<Camera>();

        // Quality settings changed in Play Mode stay in the project, so they are restored afterwards.
        bool m_StateSaved;
        RenderPipelineAsset m_OriginalPipeline;
        int m_OriginalMipLimit;
        int m_OriginalVSync;
        int m_OriginalTargetFrameRate;

        static string resultsRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BenchmarkResults"));

        void Start()
        {
#if UNITY_EDITOR
            if (SessionState.GetBool(autoRunSessionKey, false))
            {
                SessionState.EraseBool(autoRunSessionKey);
                m_ExitPlayModeWhenDone = true;
                StartBenchmark();
                return;
            }
#endif
            if (m_RunOnStart)
                StartBenchmark();
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard[m_RunKey].wasPressedThisFrame)
                StartBenchmark();
        }

        [ContextMenu("Run Benchmark")]
        public void StartBenchmark()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[Benchmark] Enter Play Mode first, or use Tools → Benchmark → Run Benchmark.");
                return;
            }
            if (m_Running)
                return;

            StartCoroutine(Run());
        }

        void OnDisable() => RestoreState();

#if UNITY_EDITOR
        void Reset()
        {
            foreach (var scenario in m_Scenarios)
            {
                var level = Array.IndexOf(QualitySettings.names, scenario.defaultLevel);
                if (level >= 0)
                    scenario.pipeline = QualitySettings.GetRenderPipelineAssetAt(level) as UniversalRenderPipelineAsset;
            }
        }
#endif

        void OnGUI()
        {
            if (!m_Running)
                return;

            if (m_Target != null)
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), m_Target, ScaleMode.ScaleToFit, false);
            GUI.Label(new Rect(10, 10, Screen.width - 20, 25), "[Benchmark] " + m_Status);
        }

        IEnumerator Run()
        {
            m_Running = true;
            var runName = DateTime.Now.ToString("yyyyMMdd_HHmmss", k_Inv);
            var runFolder = Path.Combine(resultsRoot, runName);
            Directory.CreateDirectory(Path.Combine(runFolder, "images"));
            Directory.CreateDirectory(Path.Combine(runFolder, "reference"));
            Debug.Log($"[Benchmark] Started: {m_Scenarios.Count} scenarios x {m_Views.Count} views, {m_Width}x{m_Height}. Results: {runFolder}");

            if (!FrameTimingManager.IsFeatureEnabled())
                Debug.LogWarning("[Benchmark] Frame Timing Stats are off, so CPU/GPU times are missing. Enable Player Settings → Other Settings → Frame Timing Stats.");

            var references = LoadExternalReferences();
            var rows = new List<Row>();
            var imageIndex = new StringBuilder("scenario_index,scenario,view,image,reference\n");
            var recorders = StartRecorders();

            SaveState();
            SetUpCamera();
            try
            {
                for (var s = 0; s < m_Scenarios.Count; s++)
                {
                    var scenario = m_Scenarios[s];
                    ApplyScenario(scenario);
                    yield return null;

                    foreach (var view in m_Views)
                    {
                        m_Status = $"{s + 1}/{m_Scenarios.Count} {scenario.name} – {view.name}";
                        m_Camera.transform.SetPositionAndRotation(view.position, Quaternion.Euler(view.eulerAngles));

                        for (var i = 0; i < m_WarmupFrames; i++)
                            yield return null;

                        var row = DescribeScenario(scenario, view);
                        var cpu = new List<double>();
                        var gpu = new List<double>();
                        var mainThread = new List<double>();
                        var renderThread = new List<double>();
                        var frame = new List<double>();
                        var counters = new double[recorders.Length];
                        var timings = new FrameTiming[1];

                        for (var i = 0; i < m_MeasureFrames; i++)
                        {
                            yield return null;

                            frame.Add(Time.unscaledDeltaTime * 1000.0);
                            FrameTimingManager.CaptureFrameTimings();
                            if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
                            {
                                AddIfValid(cpu, timings[0].cpuFrameTime);
                                AddIfValid(gpu, timings[0].gpuFrameTime);
                                AddIfValid(mainThread, timings[0].cpuMainThreadFrameTime);
                                AddIfValid(renderThread, timings[0].cpuRenderThreadFrameTime);
                            }
                            for (var r = 0; r < recorders.Length; r++)
                                counters[r] += recorders[r].Valid ? recorders[r].LastValue : double.NaN;
                        }

                        row.frames = m_MeasureFrames;
                        row.cpu = Stats.From(cpu);
                        row.gpu = Stats.From(gpu);
                        row.mainThread = Stats.From(mainThread);
                        row.renderThread = Stats.From(renderThread);
                        row.frame = Stats.From(frame);
                        row.triangles = counters[0] / m_MeasureFrames;
                        row.vertices = counters[1] / m_MeasureFrames;
                        row.drawCalls = counters[2] / m_MeasureFrames;
                        row.batches = counters[3] / m_MeasureFrames;
                        row.setPassCalls = counters[4] / m_MeasureFrames;
                        row.textureMemoryMB = counters[5] / m_MeasureFrames / (1024.0 * 1024.0);
                        row.gfxMemoryMB = counters[6] / m_MeasureFrames / (1024.0 * 1024.0);

                        m_Status += " – image metrics";
                        yield return null;
                        var pixels = Capture(out var image);
                        var fileName = $"{s:00}_{Sanitize(scenario.name)}_{view.name}";
                        imageIndex.AppendLine($"{s},{Quote(scenario.name)},{view.name},images/{fileName}.png,reference/{view.name}.png");

                        if (s == m_ReferenceScenario)
                        {
                            File.WriteAllBytes(Path.Combine(runFolder, "reference", view.name + ".png"), image.EncodeToPNG());
                            if (!references.ContainsKey(view.name))
                                references[view.name] = pixels;
                        }

                        if (references.TryGetValue(view.name, out var reference))
                        {
                            var map = new float[pixels.Length];
                            row.psnr = ImageMetrics.Psnr(reference, pixels);
                            row.ssim = ImageMetrics.Ssim(ImageMetrics.Luma(reference), ImageMetrics.Luma(pixels), m_Width, m_Height, map);
                            RegionSsim(map, out row.ssimFocus, out row.ssimPeriphery, out row.ssimSeam);
                            if (m_SaveImages)
                            {
                                var heatmap = ImageMetrics.SsimHeatmap(map, m_Width, m_Height);
                                File.WriteAllBytes(Path.Combine(runFolder, "images", fileName + "_ssim.png"), heatmap.EncodeToPNG());
                                Destroy(heatmap);
                            }
                        }
                        else
                        {
                            row.ssim = row.psnr = row.ssimFocus = row.ssimPeriphery = row.ssimSeam = double.NaN;
                        }

                        if (m_SaveImages)
                            File.WriteAllBytes(Path.Combine(runFolder, "images", fileName + ".png"), image.EncodeToPNG());
                        Destroy(image);

                        rows.Add(row);
                        Debug.Log(string.Format(k_Inv, "[Benchmark] {0} / {1}: GPU {2:F2} ms, CPU {3:F2} ms, frame {4:F2} ms (p99 {5:F2}), {6:F0} tris, {7:F0} draws, " +
                            "{8:F2} MPix, SSIM {9:F4} (focus {10:F4}, periphery {11:F4}, seam {12:F4}), PSNR {13:F2} dB",
                            scenario.name, view.name, row.gpu.mean, row.cpu.mean, row.frame.mean, row.frame.p99, row.triangles, row.drawCalls,
                            row.renderedMegapixels, row.ssim, row.ssimFocus, row.ssimPeriphery, row.ssimSeam, row.psnr));
                    }
                }

                File.WriteAllText(Path.Combine(runFolder, "images.csv"), imageIndex.ToString());
                WriteRunInfo(runFolder);
                WriteResults(runFolder, runName, rows);
            }
            finally
            {
                foreach (var recorder in recorders)
                    recorder.Dispose();
                RestoreState();
                m_Running = false;
                m_Status = "";
            }

#if UNITY_EDITOR
            if (m_ExitPlayModeWhenDone)
                EditorApplication.ExitPlaymode();
#endif
        }

        static void AddIfValid(List<double> list, double value)
        {
            if (value > 0 && !double.IsNaN(value))
                list.Add(value);
        }

        static ProfilerRecorder[] StartRecorders() => new[]
        {
            ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count"),
            ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count"),
            ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count"),
            ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count"),
            ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count"),
            ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Texture Memory"),
            ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Gfx Used Memory"),
        };

        Dictionary<string, Color32[]> LoadExternalReferences()
        {
            var references = new Dictionary<string, Color32[]>();
            if (string.IsNullOrWhiteSpace(m_ExternalReferenceRun))
                return references;

            var folder = Path.Combine(resultsRoot, m_ExternalReferenceRun.Trim(), "reference");
            foreach (var view in m_Views)
            {
                var path = Path.Combine(folder, view.name + ".png");
                if (!File.Exists(path))
                {
                    Debug.LogWarning($"[Benchmark] External reference not found: {path}");
                    continue;
                }

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                texture.LoadImage(File.ReadAllBytes(path));
                if (texture.width == m_Width && texture.height == m_Height)
                    references[view.name] = texture.GetPixels32();
                else
                    Debug.LogWarning($"[Benchmark] External reference {path} is {texture.width}x{texture.height}, expected {m_Width}x{m_Height}.");
                Destroy(texture);
            }

            Debug.Log($"[Benchmark] Using {references.Count} reference images from {folder}");
            return references;
        }

        void SetUpCamera()
        {
            foreach (var camera in Camera.allCameras)
            {
                camera.enabled = false;
                m_DisabledCameras.Add(camera);
            }

            var go = new GameObject("[Benchmark] Camera");
            m_Camera = go.AddComponent<Camera>();
            m_Camera.fieldOfView = m_FieldOfView;
            m_Camera.nearClipPlane = 0.05f;
            m_Camera.farClipPlane = 1000f;
            // Keep world space UI such as the performance overlay out of the images.
            m_Camera.cullingMask &= ~(1 << LayerMask.NameToLayer("UI"));
            var data = go.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.None;

            var focus = new GameObject("[Benchmark] Focus Camera");
            focus.transform.SetParent(go.transform, false);
            m_FocusCamera = focus.AddComponent<Camera>();
            m_FocusCamera.CopyFrom(m_Camera);
            m_FocusCamera.depth = m_Camera.depth - 1f;
            m_FocusCamera.enabled = false;
            var focusData = focus.AddComponent<UniversalAdditionalCameraData>();
            focusData.renderPostProcessing = true;
            focusData.antialiasing = AntialiasingMode.None;
        }

        void ApplyScenario(BenchmarkScenario scenario)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            QualitySettings.globalTextureMipmapLimit = scenario.textureMipLimit >= 0 ? scenario.textureMipLimit : m_OriginalMipLimit;

            var source = scenario.pipeline != null
                ? scenario.pipeline
                : (m_OriginalPipeline ?? GraphicsSettings.defaultRenderPipeline) as UniversalRenderPipelineAsset;
            if (m_PipelineClone != null)
                Destroy(m_PipelineClone);
            m_PipelineClone = Instantiate(source);
            m_PipelineClone.name = source.name + " (Benchmark)";
            if (scenario.renderScale > 0f)
                m_PipelineClone.renderScale = scenario.renderScale;
            if (scenario.msaa > 0)
                m_PipelineClone.msaaSampleCount = scenario.msaa;
            if (scenario.shadowResolution > 0)
                m_PipelineClone.mainLightShadowmapResolution = scenario.shadowResolution;
            if (scenario.shadowDistance >= 0f)
                m_PipelineClone.shadowDistance = scenario.shadowDistance;
            if (scenario.hdr != Override.Keep)
                m_PipelineClone.supportsHDR = scenario.hdr == Override.On;
            QualitySettings.renderPipeline = m_PipelineClone;

            m_Current = scenario;
            ReleaseTarget(m_Camera, ref m_Target);
            ReleaseTarget(m_FocusCamera, ref m_FocusTarget);

            // With quad views the main camera is the peripheral view: the whole field of view at reduced resolution,
            // scaled up when composited, as the layer does. The focus camera renders the center at full resolution
            // with its own projection, so post effects such as vignette and bloom also run per view.
            var multiplier = scenario.quadViews ? Mathf.Clamp(scenario.peripheralMultiplier, 0.05f, 1f) : 1f;
            m_Target = CreateTarget(Mathf.RoundToInt(m_Width * multiplier), Mathf.RoundToInt(m_Height * multiplier), "[Benchmark] Target");
            m_Camera.targetTexture = m_Target;

            m_FocusCamera.enabled = scenario.quadViews;
            if (scenario.quadViews)
            {
                m_FocusRect = FocusRect(scenario.focusSection);
                var focusWidth = Mathf.Max(1, Mathf.RoundToInt(m_FocusRect.width * scenario.focusMultiplier));
                var focusHeight = Mathf.Max(1, Mathf.RoundToInt(m_FocusRect.height * scenario.focusMultiplier));
                m_FocusTarget = CreateTarget(focusWidth, focusHeight, "[Benchmark] Focus Target");
                m_FocusCamera.targetTexture = m_FocusTarget;
                m_FocusCamera.projectionMatrix = FocusProjection(m_FocusRect);
            }
        }

        // An offscreen camera takes its MSAA sample count from the render texture.
        RenderTexture CreateTarget(int width, int height, string targetName)
        {
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                antiAliasing = Mathf.Max(1, m_PipelineClone.msaaSampleCount),
                name = targetName,
            };
            target.Create();
            return target;
        }

        static void ReleaseTarget(Camera camera, ref RenderTexture target)
        {
            if (camera != null)
                camera.targetTexture = null;
            if (target == null)
                return;
            target.Release();
            Destroy(target);
            target = null;
        }

        // Half angle tangents of the full view and of a focus section given as a fraction of those angles.
        void FocusAngles(Vector2 section, out float tanFullX, out float tanFullY, out float tanFocusX, out float tanFocusY)
        {
            var halfY = m_FieldOfView * 0.5f * Mathf.Deg2Rad;
            tanFullY = Mathf.Tan(halfY);
            tanFullX = tanFullY * m_Width / m_Height;
            tanFocusX = Mathf.Tan(Mathf.Atan(tanFullX) * Mathf.Clamp(section.x, 0.05f, 1f));
            tanFocusY = Mathf.Tan(halfY * Mathf.Clamp(section.y, 0.05f, 1f));
        }

        // Where the focus section lands in the final image, in whole pixels and exactly centered, so the focus view's
        // pixels line up with the full view's (a half pixel offset alone drops SSIM to about 0.8).
        RectInt FocusRect(Vector2 section)
        {
            FocusAngles(section, out var tanFullX, out var tanFullY, out var tanFocusX, out var tanFocusY);
            var marginX = Mathf.RoundToInt(m_Width * (1f - tanFocusX / tanFullX) * 0.5f);
            var marginY = Mathf.RoundToInt(m_Height * (1f - tanFocusY / tanFullY) * 0.5f);
            return new RectInt(marginX, marginY, m_Width - 2 * marginX, m_Height - 2 * marginY);
        }

        // Projection covering exactly the pixels of the focus rectangle.
        Matrix4x4 FocusProjection(RectInt rect)
        {
            var tanFullY = Mathf.Tan(m_FieldOfView * 0.5f * Mathf.Deg2Rad);
            var tanFocusX = tanFullY * m_Width / m_Height * rect.width / m_Width;
            var tanFocusY = tanFullY * rect.height / m_Height;
            var near = m_Camera.nearClipPlane;
            return Matrix4x4.Frustum(-tanFocusX * near, tanFocusX * near, -tanFocusY * near, tanFocusY * near, near, m_Camera.farClipPlane);
        }

        Row DescribeScenario(BenchmarkScenario scenario, BenchmarkView view)
        {
            var asset = m_PipelineClone;
            return new Row
            {
                scenario = scenario.name,
                view = view.name,
                pipeline = asset.name.Replace(" (Benchmark)", ""),
                renderScale = asset.renderScale,
                msaa = asset.msaaSampleCount,
                shadowResolution = asset.mainLightShadowmapResolution,
                shadowDistance = asset.shadowDistance,
                textureMipLimit = QualitySettings.globalTextureMipmapLimit,
                hdr = asset.supportsHDR,
                quadViews = scenario.quadViews,
                renderedMegapixels = RenderedPixels(m_Target) + (scenario.quadViews ? RenderedPixels(m_FocusTarget) : 0),
            };
        }

        double RenderedPixels(RenderTexture target) =>
            target.width * m_PipelineClone.renderScale * target.height * m_PipelineClone.renderScale / 1e6;

        // Mean SSIM inside the focus region, outside it, and in a band around its border.
        void RegionSsim(float[] map, out double focus, out double periphery, out double seam)
        {
            var rect = FocusRect(m_MetricFocusSection);
            var half = m_SeamWidth / 2;
            double focusSum = 0, peripherySum = 0, seamSum = 0;
            long focusCount = 0, peripheryCount = 0, seamCount = 0;
            const int border = 5;
            for (var y = border; y < m_Height - border; y++)
            {
                for (var x = border; x < m_Width - border; x++)
                {
                    // Distance to the focus rectangle border, negative inside.
                    var dx = Math.Max(rect.xMin - x, x - (rect.xMax - 1));
                    var dy = Math.Max(rect.yMin - y, y - (rect.yMax - 1));
                    var distance = dx > 0 && dy > 0 ? (int)Math.Sqrt(dx * dx + dy * dy) : Math.Max(dx, dy);
                    var value = map[y * m_Width + x];
                    if (Math.Abs(distance) <= half)
                    {
                        seamSum += value;
                        seamCount++;
                    }
                    else if (distance < 0)
                    {
                        focusSum += value;
                        focusCount++;
                    }
                    else
                    {
                        peripherySum += value;
                        peripheryCount++;
                    }
                }
            }
            focus = focusCount > 0 ? focusSum / focusCount : double.NaN;
            periphery = peripheryCount > 0 ? peripherySum / peripheryCount : double.NaN;
            seam = seamCount > 0 ? seamSum / seamCount : double.NaN;
        }

        // Capture settings for Tools/metrics/compute_metrics.py.
        void WriteRunInfo(string runFolder)
        {
            var rect = FocusRect(m_MetricFocusSection);
            var json = new StringBuilder("{\n");
            json.AppendLine(string.Format(k_Inv, "  \"width\": {0},", m_Width));
            json.AppendLine(string.Format(k_Inv, "  \"height\": {0},", m_Height));
            json.AppendLine(string.Format(k_Inv, "  \"fov_vertical\": {0},", m_FieldOfView));
            json.AppendLine(string.Format(k_Inv, "  \"focus_rect\": [{0}, {1}, {2}, {3}]", rect.x, rect.y, rect.width, rect.height));
            json.AppendLine("}");
            File.WriteAllText(Path.Combine(runFolder, "run.json"), json.ToString());
        }

        Color32[] Capture(out Texture2D image)
        {
            var pixels = Read(m_Target);
            if (m_Current.quadViews)
                pixels = Composite(pixels, m_Target.width, m_Target.height, Read(m_FocusTarget), m_FocusTarget.width, m_FocusTarget.height,
                    m_FocusRect, m_Current.focusEdgeSmoothing);

            image = new Texture2D(m_Width, m_Height, TextureFormat.RGBA32, false);
            image.SetPixels32(pixels);
            image.Apply(false);
            return pixels;
        }

        static Color32[] Read(RenderTexture target)
        {
            var resolved = RenderTexture.GetTemporary(target.width, target.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(target, resolved);

            var previous = RenderTexture.active;
            RenderTexture.active = resolved;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
            texture.Apply(false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(resolved);

            var pixels = texture.GetPixels32();
            Destroy(texture);
            return pixels;
        }

        // Scales the peripheral view up to the output size and blends the focus view into its rectangle,
        // fading over the outer edgeSmoothing fraction of the rectangle.
        Color32[] Composite(Color32[] periphery, int peripheryWidth, int peripheryHeight, Color32[] focus, int focusWidth, int focusHeight,
            RectInt rect, float edgeSmoothing)
        {
            var output = new Color32[m_Width * m_Height];
            for (var y = 0; y < m_Height; y++)
            {
                for (var x = 0; x < m_Width; x++)
                {
                    var color = Bilinear(periphery, peripheryWidth, peripheryHeight, (x + 0.5f) / m_Width, (y + 0.5f) / m_Height);
                    if (rect.Contains(new Vector2Int(x, y)))
                    {
                        var u = (x + 0.5f - rect.x) / rect.width;
                        var v = (y + 0.5f - rect.y) / rect.height;
                        var edge = Mathf.Min(Mathf.Min(u, 1f - u), Mathf.Min(v, 1f - v));
                        var weight = edgeSmoothing > 0f ? Mathf.SmoothStep(0f, 1f, edge / edgeSmoothing) : 1f;
                        color = Color.Lerp(color, Bilinear(focus, focusWidth, focusHeight, u, v), weight);
                    }
                    output[y * m_Width + x] = color;
                }
            }
            return output;
        }

        static Color Bilinear(Color32[] pixels, int width, int height, float u, float v)
        {
            var fx = Mathf.Clamp(u * width - 0.5f, 0f, width - 1f);
            var fy = Mathf.Clamp(v * height - 0.5f, 0f, height - 1f);
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
            float tx = fx - x0, ty = fy - y0;
            var top = Color.Lerp(pixels[y0 * width + x0], pixels[y0 * width + x1], tx);
            var bottom = Color.Lerp(pixels[y1 * width + x0], pixels[y1 * width + x1], tx);
            return Color.Lerp(top, bottom, ty);
        }

        void SaveState()
        {
            if (m_StateSaved)
                return;

            m_OriginalPipeline = QualitySettings.renderPipeline;
            m_OriginalMipLimit = QualitySettings.globalTextureMipmapLimit;
            m_OriginalVSync = QualitySettings.vSyncCount;
            m_OriginalTargetFrameRate = Application.targetFrameRate;
            m_StateSaved = true;
        }

        void RestoreState()
        {
            ReleaseTarget(m_FocusCamera, ref m_FocusTarget);
            ReleaseTarget(m_Camera, ref m_Target);
            if (m_Camera != null)
            {
                Destroy(m_Camera.gameObject);
                m_Camera = null;
                m_FocusCamera = null;
            }
            foreach (var camera in m_DisabledCameras)
            {
                if (camera != null)
                    camera.enabled = true;
            }
            m_DisabledCameras.Clear();

            if (!m_StateSaved)
                return;

            QualitySettings.renderPipeline = m_OriginalPipeline;
            QualitySettings.globalTextureMipmapLimit = m_OriginalMipLimit;
            QualitySettings.vSyncCount = m_OriginalVSync;
            Application.targetFrameRate = m_OriginalTargetFrameRate;

            if (m_PipelineClone != null)
            {
                Destroy(m_PipelineClone);
                m_PipelineClone = null;
            }
            m_StateSaved = false;
        }

        void WriteResults(string runFolder, string runName, List<Row> rows)
        {
            var csv = new StringBuilder();
            csv.AppendLine("scenario,view,urp_asset,render_scale,msaa,shadow_resolution,shadow_distance,texture_mip_limit,hdr,frames," +
                "gpu_ms_mean,gpu_ms_median,gpu_ms_p95,gpu_ms_p99,cpu_ms_mean,cpu_ms_median,cpu_ms_p95,cpu_ms_p99," +
                "main_thread_ms_mean,render_thread_ms_mean,frame_ms_mean,frame_ms_median,frame_ms_p95,frame_ms_p99,fps_mean," +
                "triangles,vertices,draw_calls,batches,setpass_calls,texture_memory_mb,gfx_memory_mb,quad_views,rendered_mpixels," +
                "ssim,ssim_focus,ssim_periphery,ssim_seam,psnr_db");
            foreach (var r in rows)
            {
                csv.AppendLine(string.Join(",", new[]
                {
                    Quote(r.scenario), r.view, r.pipeline, F(r.renderScale), r.msaa.ToString(k_Inv), r.shadowResolution.ToString(k_Inv),
                    F(r.shadowDistance), r.textureMipLimit.ToString(k_Inv), r.hdr ? "1" : "0", r.frames.ToString(k_Inv),
                    F(r.gpu.mean), F(r.gpu.median), F(r.gpu.p95), F(r.gpu.p99), F(r.cpu.mean), F(r.cpu.median), F(r.cpu.p95), F(r.cpu.p99),
                    F(r.mainThread.mean), F(r.renderThread.mean), F(r.frame.mean), F(r.frame.median), F(r.frame.p95), F(r.frame.p99),
                    F(1000.0 / r.frame.mean), F(r.triangles), F(r.vertices), F(r.drawCalls), F(r.batches), F(r.setPassCalls),
                    F(r.textureMemoryMB), F(r.gfxMemoryMB), r.quadViews ? "1" : "0", F(r.renderedMegapixels),
                    F(r.ssim), F(r.ssimFocus), F(r.ssimPeriphery), F(r.ssimSeam), F(r.psnr),
                }));
            }
            File.WriteAllText(Path.Combine(runFolder, "results.csv"), csv.ToString());

            // Summary: mean over views per scenario.
            var scenarios = rows.GroupBy(r => r.scenario).Select(g => new
            {
                name = g.Key,
                gpu = g.Average(r => r.gpu.mean),
                cpu = g.Average(r => r.cpu.mean),
                frame = g.Average(r => r.frame.mean),
                p99 = g.Average(r => r.frame.p99),
                tris = g.Average(r => r.triangles),
                draws = g.Average(r => r.drawCalls),
                texMem = g.Average(r => r.textureMemoryMB),
                mpix = g.Average(r => r.renderedMegapixels),
                ssimFocus = g.Average(r => r.ssimFocus),
                ssimPeriphery = g.Average(r => r.ssimPeriphery),
                ssimSeam = g.Average(r => r.ssimSeam),
                ssim = g.Average(r => r.ssim),
                psnr = g.Average(r => double.IsInfinity(r.psnr) ? double.NaN : r.psnr),
                ssimMin = g.Min(r => r.ssim),
            }).ToList();
            var baseline = m_BaselineScenario >= 0 && m_BaselineScenario < scenarios.Count ? scenarios[m_BaselineScenario] : null;

            var md = new StringBuilder();
            md.AppendLine($"# Benchmark {runName}");
            md.AppendLine();
            md.AppendLine($"- {Application.platform}, {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}), {SystemInfo.processorType}");
            md.AppendLine($"- Unity {Application.unityVersion}, {(Application.isEditor ? "Editor (Play Mode)" : Debug.isDebugBuild ? "Development build" : "Release build")}");
            md.AppendLine($"- {m_Width}x{m_Height}, FOV {m_FieldOfView:F0}, {m_Views.Count} views, {m_WarmupFrames} warmup + {m_MeasureFrames} measured frames per view, VSync off");
            md.AppendLine($"- Reference for SSIM/PSNR: {(string.IsNullOrWhiteSpace(m_ExternalReferenceRun) ? m_Scenarios[m_ReferenceScenario].name : "run " + m_ExternalReferenceRun)}");
            if (baseline != null)
                md.AppendLine($"- Frame time change relative to: {baseline.name}");
            md.AppendLine();
            md.AppendLine("Values are the mean over all views. Frame p99 is the 99th percentile frame time (\"1% low\").");
            md.AppendLine();
            md.AppendLine("SSIM focus / periphery / seam split the image by the metric focus region " +
                string.Format(k_Inv, "({0:0.##} x {1:0.##} of the field of view, seam band {2} px).", m_MetricFocusSection.x, m_MetricFocusSection.y, m_SeamWidth));
            md.AppendLine();
            md.AppendLine("| Scenario | GPU ms | CPU ms | Frame ms | Frame p99 ms | Δ GPU | Rendered MPix | Triangles | Draw calls | Texture MB | SSIM | SSIM (worst view) | SSIM focus | SSIM periphery | SSIM seam | PSNR dB |");
            md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var s in scenarios)
            {
                var delta = baseline != null && baseline.gpu > 0 ? ((s.gpu / baseline.gpu - 1.0) * 100.0).ToString("+0;-0;0", k_Inv) + "%" : "";
                md.AppendLine($"| {s.name} | {D(s.gpu, "F2")} | {D(s.cpu, "F2")} | {D(s.frame, "F2")} | {D(s.p99, "F2")} | {delta} | {D(s.mpix, "F2")} | {D(s.tris, "N0")} | " +
                    $"{D(s.draws, "F0")} | {D(s.texMem, "F0")} | {D(s.ssim, "F4")} | {D(s.ssimMin, "F4")} | {D(s.ssimFocus, "F4")} | {D(s.ssimPeriphery, "F4")} | {D(s.ssimSeam, "F4")} | {D(s.psnr, "F2")} |");
            }
            File.WriteAllText(Path.Combine(runFolder, "summary.md"), md.ToString());

            Debug.Log($"[Benchmark] Done. Results: {runFolder}\n{md}");
        }

        static string F(double value) =>
            double.IsNaN(value) ? "" : double.IsPositiveInfinity(value) ? "inf" : value.ToString("0.####", k_Inv);

        static string D(double value, string format) => double.IsNaN(value) ? "–" : value.ToString(format, k_Inv);

        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

        static string Sanitize(string value)
        {
            var sb = new StringBuilder();
            foreach (var c in value)
                sb.Append(char.IsLetterOrDigit(c) || c == '.' ? c : '_');
            return sb.ToString().Trim('_');
        }
    }
}
