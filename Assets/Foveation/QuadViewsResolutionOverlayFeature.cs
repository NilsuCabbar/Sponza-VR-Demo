using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Foveation
{
    /// <summary>
    /// Colors every view by the resolution it is rendered at (key R, see <see cref="QuadViewsResolution"/>).
    ///
    /// In the headset each quad view is a separate XR pass, so the periphery and the focus view get their own
    /// translucent color; the Quad-Views-Foveated layer then composites them, and the blended edge of
    /// the focus view shows as a color gradient. Without a headset the whole screen is one full resolution view; the
    /// desktop quad views simulation draws its own colors (QuadViewsSimulator.shader).
    /// </summary>
    public class QuadViewsResolutionOverlayFeature : ScriptableRendererFeature
    {
        [SerializeField]
        Shader m_Shader;

        Material m_Material;
        OverlayPass m_Pass;

        public override void Create()
        {
            if (m_Shader == null)
                m_Shader = Shader.Find("Hidden/Foveation/ResolutionOverlay");
            m_Pass = new OverlayPass { renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!QuadViewsResolution.show || m_Shader == null)
                return;

            ref var cameraData = ref renderingData.cameraData;
            if (cameraData.cameraType != CameraType.Game)
                return;

            Color color;
            if (cameraData.xr != null && cameraData.xr.enabled)
            {
                var views = QuadViewsResolution.GetViews(cameraData.camera);
                var index = cameraData.xr.multipassId;
                if (index < 0 || index >= views.Count)
                    return;
                color = views[index].color;
            }
            else
            {
                // Desktop: the simulation colors its own composite; its cameras render into textures.
                if (QuadViewsSimulator.isSimulating || cameraData.camera.targetTexture != null)
                    return;
                color = QuadViewsResolution.ColorForRatio(1f);
            }

            if (m_Material == null)
                m_Material = CoreUtils.CreateEngineMaterial(m_Shader);
            m_Pass.Setup(m_Material, color);
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(m_Material);
            m_Material = null;
        }

        class OverlayPass : ScriptableRenderPass
        {
            static readonly int k_Color = Shader.PropertyToID("_OverlayColor");

            Material m_Material;
            Color m_Color;

            class PassData
            {
                public Material material;
                public MaterialPropertyBlock properties;
            }

            public void Setup(Material material, Color color)
            {
                m_Material = material;
                m_Color = color;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resourceData = frameData.Get<UniversalResourceData>();
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Quad Views Resolution Colors", out var passData))
                {
                    // Every XR pass records its own color, so it goes in a property block, not the shared material.
                    passData.material = m_Material;
                    passData.properties = new MaterialPropertyBlock();
                    passData.properties.SetColor(k_Color, m_Color);

                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                        context.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, data.properties));
                }
            }
        }
    }
}
