using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Foveation
{
    /// <summary>
    /// Resolution of every view Unity renders for the headset, for the "resolution colors" display (key R).
    ///
    /// Each view's pixel density is its width divided by the tangent span of its field of view. Quad views render a wide
    /// low density periphery and a narrow full density focus view per eye; without quad views both eyes are full density.
    /// Densities are compared to the highest one in the frame, which is the display's native density by default
    /// (focus_multiplier 1.0).
    /// </summary>
    public static class QuadViewsResolution
    {
        public struct View
        {
            public int width;
            public int height;
            /// <summary>Pixels per unit of view tangent, horizontally.</summary>
            public float density;
            /// <summary>Density relative to the densest view of the frame (1 = full resolution).</summary>
            public float ratio;
            public Color color;
        }

        /// <summary>Whether the resolution colors are shown. Set by <see cref="QuadViewsSimulator"/> (key R).</summary>
        public static bool show;

        /// <summary>Opacity of the colors.</summary>
        public static float opacity = 0.3f;

        /// <summary>Legend of the desktop simulation, set by <see cref="QuadViewsSimulator"/> while it runs.</summary>
        public static string desktopLegend;

        static readonly List<View> s_Views = new List<View>();
        static readonly List<XRDisplaySubsystem> s_Displays = new List<XRDisplaySubsystem>();
        static int s_Frame = -1;

        /// <summary>
        /// Color for a density ratio, the same colors as the VRS overlay of Sponza_Intel_Foveated_Rendering_Shader where a
        /// ratio is one pixel per NxN screen pixels: green 1x1 (full), yellow-green, yellow 2x2 (half), orange, red 4x4.
        /// </summary>
        public static Color ColorForRatio(float ratio)
        {
            if (ratio >= 0.9f) return new Color(0f, 1f, 0f, opacity);
            if (ratio >= 0.65f) return new Color(0.6f, 1f, 0f, opacity);
            if (ratio >= 0.4f) return new Color(1f, 1f, 0f, opacity);
            if (ratio >= 0.3f) return new Color(1f, 0.5f, 0f, opacity);
            return new Color(1f, 0f, 0f, opacity);
        }

        /// <summary>One rendered pixel covers about NxN display pixels, as text ("1x1", "2x2", "1.5x1.5").</summary>
        public static string PixelSize(float ratio)
        {
            var n = ratio > 0f ? 1f / ratio : 0f;
            var text = Mathf.Abs(n - Mathf.Round(n)) < 0.05f ? Mathf.Round(n).ToString("0") : n.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            return $"{text}x{text}";
        }

        /// <summary>Legend lines for the performance overlay: one per distinct view, in its color.</summary>
        public static string Legend(Camera camera)
        {
            var views = GetViews(camera);
            if (views.Count == 0)
                return desktopLegend;
            var sb = new System.Text.StringBuilder();
            var seen = new HashSet<(int, int)>();
            foreach (var view in views)
            {
                if (!seen.Add((view.width, view.height)))
                    continue;
                var kind = views.Count == 4 ? (view.ratio >= 0.99f ? "focus" : "periphery") : "eye";
                sb.AppendLine(LegendLine(view.color, kind, view.width, view.height, view.ratio));
            }
            return sb.ToString().TrimEnd();
        }

        public static string LegendLine(Color color, string kind, int width, int height, float ratio) =>
            $"<color=#{Hex(color)}>■ {kind} {width}x{height}  {ratio * 100f:0}%  ({PixelSize(ratio)})</color>";

        public static string Hex(Color color) => ColorUtility.ToHtmlStringRGB(color);

        /// <summary>
        /// The headset's views in the order URP renders them in multi-pass (one XR pass per view, index = multipassId).
        /// Empty without a running headset.
        /// </summary>
        public static IReadOnlyList<View> GetViews(Camera camera)
        {
            if (s_Frame == Time.frameCount || camera == null)
                return s_Views;
            s_Frame = Time.frameCount;
            s_Views.Clear();

            SubsystemManager.GetSubsystems(s_Displays);
            XRDisplaySubsystem display = null;
            foreach (var d in s_Displays)
                if (d.running)
                    display = d;
            if (display == null)
                return s_Views;

            var max = 0f;
            for (var p = 0; p < display.GetRenderPassCount(); p++)
            {
                display.GetRenderPass(p, out var pass);
                for (var i = 0; i < pass.GetRenderParameterCount(); i++)
                {
                    pass.GetRenderParameter(camera, i, out var parameter);
                    var width = Mathf.RoundToInt(parameter.viewport.width * pass.renderTargetScaledWidth);
                    var height = Mathf.RoundToInt(parameter.viewport.height * pass.renderTargetScaledHeight);
                    // m00 = 2 / (tanRight - tanLeft), so pixels per tangent unit = width * m00 / 2.
                    var density = width * parameter.projection.m00 * 0.5f;
                    max = Mathf.Max(max, density);
                    s_Views.Add(new View { width = width, height = height, density = density });
                }
            }

            for (var i = 0; i < s_Views.Count; i++)
            {
                var view = s_Views[i];
                view.ratio = max > 0f ? view.density / max : 1f;
                view.color = ColorForRatio(view.ratio);
                s_Views[i] = view;
            }
            return s_Views;
        }
    }
}
