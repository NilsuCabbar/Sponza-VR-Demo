// Tints a whole view with the translucent color of its resolution. Used by QuadViewsResolutionOverlayFeature (key R).
Shader "Hidden/Foveation/ResolutionOverlay"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "ResolutionOverlay"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            float4 _OverlayColor;   // rgb, a = opacity

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                return _OverlayColor;
            }
            ENDHLSL
        }
    }
}
