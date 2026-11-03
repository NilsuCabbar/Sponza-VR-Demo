// Composites the two views of the desktop quad views simulation, like the Quad-Views-Foveated layer does in the
// headset: the reduced resolution peripheral view scaled to the screen, with the full resolution focus view blended
// into its rectangle. Optionally outlines the focus region, tints the periphery and marks the gaze point.
Shader "Hidden/Foveation/QuadViewsSimulator"
{
    Properties
    {
        _MainTex ("Peripheral view", 2D) = "black" {}
        _FocusTex ("Focus view", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "Queue" = "Overlay" "IgnoreProjector" = "True" "RenderType" = "Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _FocusTex;
            float4 _FocusTex_TexelSize;

            float4 _FocusRect;      // xMin, yMin, xMax, yMax in 0..1 screen coordinates
            float2 _Gaze;           // gaze point in 0..1 screen coordinates
            float _Smoothing;       // smoothen_focus_view_edges: blend width as a fraction of the focus region
            float _Sharpen;         // sharpen_focus_view
            float _ShowRegion;
            float _PeripheryTint;
            float4 _ScreenSizePx;   // output size in pixels

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float3 SampleFocus(float2 uv)
            {
                float3 color = tex2D(_FocusTex, uv).rgb;
                if (_Sharpen <= 0)
                    return color;

                // Contrast adaptive sharpening (AMD FidelityFX CAS, cross pattern), the method the layer uses.
                // It sharpens less where local contrast is already high and stays within the neighbors' range,
                // so it cannot create new dark or bright specks. Works in approximate gamma space like CAS.
                float2 t = _FocusTex_TexelSize.xy;
                float3 c = sqrt(color);
                float3 n = sqrt(tex2D(_FocusTex, uv + float2(0, t.y)).rgb);
                float3 s = sqrt(tex2D(_FocusTex, uv - float2(0, t.y)).rgb);
                float3 e = sqrt(tex2D(_FocusTex, uv + float2(t.x, 0)).rgb);
                float3 w = sqrt(tex2D(_FocusTex, uv - float2(t.x, 0)).rgb);

                float3 minRgb = min(c, min(min(n, s), min(e, w)));
                float3 maxRgb = max(c, max(max(n, s), max(e, w)));
                float3 amplitude = sqrt(saturate(min(minRgb, 1 - maxRgb) / max(maxRgb, 1e-4)));
                float peak = -1.0 / lerp(8.0, 5.0, saturate(_Sharpen));
                float3 weight = amplitude * peak;
                float3 sharpened = (c + (n + s + e + w) * weight) / (1 + 4 * weight);
                sharpened = clamp(sharpened, minRgb, maxRgb);
                return sharpened * sharpened;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 color = tex2D(_MainTex, i.uv).rgb;
                float2 size = _FocusRect.zw - _FocusRect.xy;
                float2 f = (i.uv - _FocusRect.xy) / size;
                bool inside = all(f >= 0) && all(f <= 1);

                if (inside)
                {
                    float edge = min(min(f.x, 1 - f.x), min(f.y, 1 - f.y));
                    float weight = _Smoothing > 0 ? smoothstep(0, _Smoothing, edge) : 1;
                    color = lerp(color, SampleFocus(f), weight);
                }

                if (_ShowRegion > 0)
                {
                    float2 pixel = i.uv * _ScreenSizePx.xy;
                    float2 rectMin = _FocusRect.xy * _ScreenSizePx.xy;
                    float2 rectMax = _FocusRect.zw * _ScreenSizePx.xy;

                    if (!inside)
                        color = lerp(color, float3(0.15, 0.3, 1.0), _PeripheryTint);

                    // 2 px green outline on the focus rectangle and a dashed yellow line where the blend starts.
                    float2 d = min(abs(pixel - rectMin), abs(pixel - rectMax));
                    bool onX = pixel.y >= rectMin.y - 1 && pixel.y <= rectMax.y + 1;
                    bool onY = pixel.x >= rectMin.x - 1 && pixel.x <= rectMax.x + 1;
                    if ((d.x < 2 && onX) || (d.y < 2 && onY))
                        color = float3(0.2, 1.0, 0.2);

                    if (inside && _Smoothing > 0)
                    {
                        float edge = min(min(f.x, 1 - f.x), min(f.y, 1 - f.y));
                        float dash = step(0.5, frac((pixel.x + pixel.y) / 12));
                        if (abs(edge - _Smoothing) * min(size.x * _ScreenParams.x, size.y * _ScreenParams.y) < 1 && dash > 0)
                            color = float3(1.0, 0.9, 0.2);
                    }

                    // Gaze crosshair.
                    float2 g = abs(pixel - _Gaze * _ScreenSizePx.xy);
                    if ((g.x < 1.5 && g.y < 12) || (g.y < 1.5 && g.x < 12))
                        color = float3(1.0, 0.2, 0.2);
                }

                return float4(color, 1);
            }
            ENDCG
        }
    }
}
