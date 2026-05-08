using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Sumo.Rendering
{
    [Serializable]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    [VolumeComponentMenu("Post-processing/Sumo/Comic Style")]
    public sealed class ComicStyleVolumeComponent : VolumeComponent, IPostProcessComponent
    {
        [Header("Core")]
        public BoolParameter enabled = new BoolParameter(true);
        public ClampedFloatParameter posterizeStrength = new ClampedFloatParameter(0.36f, 0f, 1f);
        public ClampedIntParameter paletteSteps = new ClampedIntParameter(7, 2, 16);

        [Header("Print")]
        public ClampedFloatParameter halftoneScale = new ClampedFloatParameter(0f, 0f, 28f);
        public ClampedFloatParameter halftoneIntensity = new ClampedFloatParameter(0f, 0f, 1f);
        public ClampedFloatParameter hatchIntensity = new ClampedFloatParameter(0f, 0f, 1f);

        [Header("Bloom")]
        public ClampedFloatParameter bloomThreshold = new ClampedFloatParameter(0.72f, 0f, 4f);
        public ClampedFloatParameter bloomIntensity = new ClampedFloatParameter(0.12f, 0f, 2f);

        [Header("Ink")]
        public ClampedFloatParameter outlineStrength = new ClampedFloatParameter(1f, 0f, 2f);
        public ClampedFloatParameter screenOutlineStrength = new ClampedFloatParameter(1.15f, 0f, 3f);
        public ClampedFloatParameter screenOutlineThickness = new ClampedFloatParameter(1.35f, 0.5f, 4f);
        public ClampedFloatParameter screenOutlineDepthSensitivity = new ClampedFloatParameter(18f, 0f, 64f);
        public ClampedFloatParameter screenOutlineNormalSensitivity = new ClampedFloatParameter(1.8f, 0f, 8f);

        [Header("Paint")]
        public ClampedFloatParameter paintPatchStrength = new ClampedFloatParameter(0.3f, 0f, 1f);

        public bool IsActive()
        {
            return enabled.value
                   && (posterizeStrength.value > 0f
                       || halftoneIntensity.value > 0f
                       || hatchIntensity.value > 0f
                       || bloomIntensity.value > 0f
                       || outlineStrength.value > 0f
                       || screenOutlineStrength.value > 0f
                       || paintPatchStrength.value > 0f);
        }

        [Obsolete("Unused #from(2023.1)")]
        public bool IsTileCompatible() => false;
    }
}
