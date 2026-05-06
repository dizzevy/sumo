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
        public ClampedFloatParameter halftoneScale = new ClampedFloatParameter(9.5f, 3f, 28f);
        public ClampedFloatParameter halftoneIntensity = new ClampedFloatParameter(0.14f, 0f, 1f);
        public ClampedFloatParameter hatchIntensity = new ClampedFloatParameter(0.12f, 0f, 1f);

        [Header("Bloom")]
        public ClampedFloatParameter bloomThreshold = new ClampedFloatParameter(0.72f, 0f, 4f);
        public ClampedFloatParameter bloomIntensity = new ClampedFloatParameter(0.12f, 0f, 2f);

        [Header("Ink")]
        public ClampedFloatParameter outlineStrength = new ClampedFloatParameter(1f, 0f, 2f);

        public bool IsActive()
        {
            return enabled.value
                   && (posterizeStrength.value > 0f
                       || halftoneIntensity.value > 0f
                       || hatchIntensity.value > 0f
                       || bloomIntensity.value > 0f
                       || outlineStrength.value > 0f);
        }

        [Obsolete("Unused #from(2023.1)")]
        public bool IsTileCompatible() => false;
    }
}
