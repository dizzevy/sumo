using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;
using static UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils;

namespace Sumo.Rendering
{
    public sealed class ComicRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] private bool renderOutlines = true;
        [SerializeField] private bool renderComposite = true;
        [SerializeField] private bool liteMode;
        [SerializeField] private LayerMask layerMask = ~0;

        private const string CompositeShaderName = "Hidden/Sumo/ComicComposite";
        private Material _compositeMaterial;
        private OutlinePass _outlinePass;
        private CompositePass _compositePass;

        public override void Create()
        {
            _outlinePass ??= new OutlinePass(layerMask);
            _outlinePass.Configure(renderOutlines, layerMask);

            Shader shader = Shader.Find(CompositeShaderName);
            if (shader != null && (_compositeMaterial == null || _compositeMaterial.shader != shader))
            {
                CoreUtils.Destroy(_compositeMaterial);
                _compositeMaterial = CoreUtils.CreateEngineMaterial(shader);
            }

            _compositePass ??= new CompositePass();
            _compositePass.Configure(renderComposite, liteMode, _compositeMaterial);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
            {
                return;
            }

            if (renderOutlines && _outlinePass != null)
            {
                _outlinePass.Configure(renderOutlines, layerMask);
                renderer.EnqueuePass(_outlinePass);
            }

            if (renderComposite && _compositePass != null && _compositeMaterial != null)
            {
                _compositePass.Configure(renderComposite, liteMode, _compositeMaterial);
                renderer.EnqueuePass(_compositePass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_compositeMaterial);
            _compositeMaterial = null;
        }

        private sealed class OutlinePass : ScriptableRenderPass
        {
            private static readonly ShaderTagId ComicOutlineTag = new ShaderTagId("ComicOutline");
            private readonly List<ShaderTagId> _shaderTags = new List<ShaderTagId> { ComicOutlineTag };
            private FilteringSettings _filteringSettings;
            private bool _enabled;

            public OutlinePass(LayerMask layerMask)
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
                profilingSampler = new ProfilingSampler("Sumo Comic Outlines");
                _filteringSettings = new FilteringSettings(RenderQueueRange.all, layerMask);
            }

            public void Configure(bool enabled, LayerMask layerMask)
            {
                _enabled = enabled;
                _filteringSettings = new FilteringSettings(RenderQueueRange.all, layerMask);
            }

            private sealed class PassData
            {
                public RendererListHandle rendererList;
                public TextureHandle color;
                public TextureHandle depth;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!_enabled)
                {
                    return;
                }

                ComicStyleVolumeComponent settings = VolumeManager.instance.stack.GetComponent<ComicStyleVolumeComponent>();
                if (settings == null || !settings.enabled.value || settings.outlineStrength.value <= 0f)
                {
                    return;
                }

                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();

                if (!resources.activeColorTexture.IsValid())
                {
                    return;
                }

                using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<PassData>(
                           "Sumo Comic Outlines",
                           out PassData passData,
                           profilingSampler))
                {
                    DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(
                        _shaderTags,
                        renderingData,
                        cameraData,
                        lightData,
                        SortingCriteria.CommonTransparent);

                    RendererListParams rendererListParams = new RendererListParams(
                        renderingData.cullResults,
                        drawingSettings,
                        _filteringSettings);

                    passData.rendererList = renderGraph.CreateRendererList(rendererListParams);
                    builder.UseRendererList(passData.rendererList);

                    passData.color = resources.activeColorTexture;
                    builder.SetRenderAttachment(passData.color, 0, AccessFlags.Write);

                    if (resources.activeDepthTexture.IsValid())
                    {
                        passData.depth = resources.activeDepthTexture;
                        builder.SetRenderAttachmentDepth(passData.depth, AccessFlags.Read);
                    }

                    builder.AllowGlobalStateModification(true);
                    builder.UseAllGlobalTextures(true);
                    builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                    {
                        context.cmd.DrawRendererList(data.rendererList);
                    });
                }
            }
        }

        private sealed class CompositePass : ScriptableRenderPass
        {
            private static readonly int PosterizeStrengthId = Shader.PropertyToID("_ComicPosterizeStrength");
            private static readonly int PaletteStepsId = Shader.PropertyToID("_ComicPaletteSteps");
            private static readonly int HalftoneScaleId = Shader.PropertyToID("_ComicHalftoneScale");
            private static readonly int HalftoneIntensityId = Shader.PropertyToID("_ComicHalftoneIntensity");
            private static readonly int HatchIntensityId = Shader.PropertyToID("_ComicHatchIntensity");
            private static readonly int BloomThresholdId = Shader.PropertyToID("_ComicBloomThreshold");
            private static readonly int BloomIntensityId = Shader.PropertyToID("_ComicBloomIntensity");
            private static readonly int OutlineStrengthId = Shader.PropertyToID("_ComicOutlineStrength");
            private static readonly int LiteModeId = Shader.PropertyToID("_ComicLiteMode");

            private Material _material;
            private bool _enabled;
            private bool _liteMode;

            public CompositePass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
                profilingSampler = new ProfilingSampler("Sumo Comic Composite");
                requiresIntermediateTexture = true;
            }

            public void Configure(bool enabled, bool liteMode, Material material)
            {
                _enabled = enabled;
                _liteMode = liteMode;
                _material = material;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!_enabled || _material == null)
                {
                    return;
                }

                ComicStyleVolumeComponent settings = VolumeManager.instance.stack.GetComponent<ComicStyleVolumeComponent>();
                if (settings == null || !settings.IsActive())
                {
                    return;
                }

                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (!resources.activeColorTexture.IsValid() || resources.isActiveTargetBackBuffer)
                {
                    return;
                }

                float liteScale = _liteMode ? 0.55f : 1f;
                _material.SetFloat(PosterizeStrengthId, settings.posterizeStrength.value);
                _material.SetFloat(PaletteStepsId, settings.paletteSteps.value);
                _material.SetFloat(HalftoneScaleId, settings.halftoneScale.value);
                _material.SetFloat(HalftoneIntensityId, settings.halftoneIntensity.value * liteScale);
                _material.SetFloat(HatchIntensityId, settings.hatchIntensity.value * liteScale);
                _material.SetFloat(BloomThresholdId, settings.bloomThreshold.value);
                _material.SetFloat(BloomIntensityId, _liteMode ? 0f : settings.bloomIntensity.value);
                _material.SetFloat(OutlineStrengthId, settings.outlineStrength.value);
                _material.SetFloat(LiteModeId, _liteMode ? 1f : 0f);

                TextureHandle source = resources.activeColorTexture;
                TextureDesc destinationDesc = renderGraph.GetTextureDesc(source);
                destinationDesc.name = "_SumoComicCompositeColor";
                destinationDesc.clearBuffer = false;
                TextureHandle destination = renderGraph.CreateTexture(destinationDesc);

                BlitMaterialParameters blitParameters = new BlitMaterialParameters(source, destination, _material, 0);
                renderGraph.AddBlitPass(blitParameters, "Sumo Comic Composite");
                resources.cameraColor = destination;
            }
        }
    }
}
