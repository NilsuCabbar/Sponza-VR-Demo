using System;
using UnityEngine;

namespace Benchmark
{
    /// <summary>
    /// Full-reference image quality metrics: PSNR and SSIM (Wang et al. 2004, "Image Quality Assessment:
    /// From Error Visibility to Structural Similarity"). SSIM uses the standard parameters: 11x11 Gaussian
    /// window with sigma 1.5, K1 = 0.01, K2 = 0.03, L = 255, computed on luma.
    /// </summary>
    public static class ImageMetrics
    {
        const int k_Radius = 5;
        const float k_Sigma = 1.5f;
        const float k_C1 = (0.01f * 255f) * (0.01f * 255f);
        const float k_C2 = (0.03f * 255f) * (0.03f * 255f);

        /// <summary>BT.601 luma in 0..255, the same conversion as MATLAB rgb2gray used by the reference SSIM code.</summary>
        public static float[] Luma(Color32[] pixels)
        {
            var luma = new float[pixels.Length];
            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                luma[i] = 0.299f * p.r + 0.587f * p.g + 0.114f * p.b;
            }
            return luma;
        }

        /// <summary>Peak signal-to-noise ratio over the RGB channels, in dB. Identical images return +infinity.</summary>
        public static double Psnr(Color32[] a, Color32[] b)
        {
            if (a.Length != b.Length)
                throw new ArgumentException("Images must have the same size.");

            double sum = 0;
            for (var i = 0; i < a.Length; i++)
            {
                double dr = a[i].r - b[i].r, dg = a[i].g - b[i].g, db = a[i].b - b[i].b;
                sum += dr * dr + dg * dg + db * db;
            }

            var mse = sum / (a.Length * 3.0);
            return mse <= 0 ? double.PositiveInfinity : 10.0 * Math.Log10(255.0 * 255.0 / mse);
        }

        /// <summary>
        /// Mean SSIM of two luma images. Pixels closer than the window radius to the border are left out of the
        /// mean, like the "valid" mode of the reference implementation. <paramref name="map"/>, when given,
        /// receives the per-pixel SSIM.
        /// </summary>
        public static double Ssim(float[] x, float[] y, int width, int height, float[] map = null)
        {
            var n = width * height;
            if (x.Length != n || y.Length != n)
                throw new ArgumentException("Images must have the same size.");

            var kernel = GaussianKernel();
            var xx = new float[n];
            var yy = new float[n];
            var xy = new float[n];
            for (var i = 0; i < n; i++)
            {
                xx[i] = x[i] * x[i];
                yy[i] = y[i] * y[i];
                xy[i] = x[i] * y[i];
            }

            var tmp = new float[n];
            var muX = Blur(x, tmp, width, height, kernel);
            var muY = Blur(y, tmp, width, height, kernel);
            var sXX = Blur(xx, tmp, width, height, kernel);
            var sYY = Blur(yy, tmp, width, height, kernel);
            var sXY = Blur(xy, tmp, width, height, kernel);

            double sum = 0;
            long count = 0;
            for (var py = 0; py < height; py++)
            {
                var inY = py >= k_Radius && py < height - k_Radius;
                for (var px = 0; px < width; px++)
                {
                    var i = py * width + px;
                    float mx = muX[i], my = muY[i];
                    var varX = sXX[i] - mx * mx;
                    var varY = sYY[i] - my * my;
                    var cov = sXY[i] - mx * my;
                    var s = (2f * mx * my + k_C1) * (2f * cov + k_C2) / ((mx * mx + my * my + k_C1) * (varX + varY + k_C2));

                    if (map != null)
                        map[i] = s;
                    if (inY && px >= k_Radius && px < width - k_Radius)
                    {
                        sum += s;
                        count++;
                    }
                }
            }

            return count > 0 ? sum / count : 1.0;
        }

        /// <summary>Per-pixel SSIM as an image: black where identical, red to yellow where structure differs.</summary>
        public static Texture2D SsimHeatmap(float[] map, int width, int height)
        {
            var pixels = new Color32[map.Length];
            for (var i = 0; i < map.Length; i++)
            {
                // Scale so that SSIM 0.5 and below is fully saturated.
                var d = Mathf.Clamp01((1f - map[i]) * 2f);
                pixels[i] = new Color(Mathf.Clamp01(d * 2f), Mathf.Clamp01(d * 2f - 1f), 0f, 1f);
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        static float[] GaussianKernel()
        {
            var kernel = new float[k_Radius * 2 + 1];
            float sum = 0;
            for (var i = -k_Radius; i <= k_Radius; i++)
            {
                kernel[i + k_Radius] = Mathf.Exp(-(i * i) / (2f * k_Sigma * k_Sigma));
                sum += kernel[i + k_Radius];
            }
            for (var i = 0; i < kernel.Length; i++)
                kernel[i] /= sum;
            return kernel;
        }

        // Separable Gaussian blur with clamped edges.
        static float[] Blur(float[] src, float[] tmp, int width, int height, float[] kernel)
        {
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    float acc = 0;
                    for (var k = -k_Radius; k <= k_Radius; k++)
                        acc += kernel[k + k_Radius] * src[row + Mathf.Clamp(x + k, 0, width - 1)];
                    tmp[row + x] = acc;
                }
            }

            var dst = new float[src.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    float acc = 0;
                    for (var k = -k_Radius; k <= k_Radius; k++)
                        acc += kernel[k + k_Radius] * tmp[Mathf.Clamp(y + k, 0, height - 1) * width + x];
                    dst[y * width + x] = acc;
                }
            }
            return dst;
        }
    }
}
