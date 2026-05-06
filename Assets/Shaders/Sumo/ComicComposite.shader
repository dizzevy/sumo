Shader "Hidden/Sumo/ComicComposite"
{
    HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/DynamicScalingClamping.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        float _ComicPosterizeStrength;
        float _ComicPaletteSteps;
        float _ComicHalftoneScale;
        float _ComicHalftoneIntensity;
        float _ComicHatchIntensity;
        float _ComicBloomThreshold;
        float _ComicBloomIntensity;
        float _ComicOutlineStrength;
        float _ComicLiteMode;

        half MaxChannel(half3 color)
        {
            return max(color.r, max(color.g, color.b));
        }

        half3 Posterize(half3 color)
        {
            float steps = max(2.0, _ComicPaletteSteps);
            half value = MaxChannel(color);
            half posterizedValue = round(value * steps) / steps;
            half scale = value > 0.0001h ? posterizedValue / value : 0.0h;
            return saturate(color * scale);
        }

        half Halftone(float2 pixelPosition, half luminance)
        {
            float scale = max(3.0, _ComicHalftoneScale);
            float2 cell = frac(pixelPosition / scale) - 0.5;
            float radius = lerp(0.12, 0.43, saturate(1.0 - luminance));
            return step(length(cell), radius);
        }

        half Hatch(float2 pixelPosition, half luminance)
        {
            float hatchScale = lerp(13.0, 8.0, saturate(_ComicHatchIntensity));
            float lineA = step(frac((pixelPosition.x + pixelPosition.y) / hatchScale), 0.09);
            float lineB = step(frac((pixelPosition.x - pixelPosition.y) / (hatchScale * 1.35)), 0.055);
            half shadowMask = saturate((0.62h - luminance) * 2.1h);
            return saturate((lineA + lineB * 0.7h) * shadowMask);
        }

        half3 BloomSample(float2 uv)
        {
            half3 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, ClampAndScaleUVForBilinear(uv)).rgb;
            half value = MaxChannel(color);
            half glow = saturate((value - _ComicBloomThreshold) / max(0.001h, 1.0h - _ComicBloomThreshold));
            return color * glow;
        }

        half3 CheapComicBloom(float2 uv, half3 color)
        {
            half value = MaxChannel(color);
            half glow = saturate((value - _ComicBloomThreshold) / max(0.001h, 1.0h - _ComicBloomThreshold));
            half3 bloom = color * glow;
            float2 texel = _BlitTexture_TexelSize.xy;
            bloom += BloomSample(uv + texel * float2(3.0, 0.0));
            bloom += BloomSample(uv + texel * float2(-3.0, 0.0));
            bloom += BloomSample(uv + texel * float2(0.0, 3.0));
            bloom += BloomSample(uv + texel * float2(0.0, -3.0));
            bloom *= 0.2h;
            return bloom * _ComicBloomIntensity;
        }

        half4 FragComicComposite(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 uv = ClampAndScaleUVForBilinear(input.texcoord);
            half4 source = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
            half3 color = source.rgb;

            half3 posterized = Posterize(color);
            color = lerp(color, posterized, saturate(_ComicPosterizeStrength));

            half luminance = dot(color, half3(0.2126h, 0.7152h, 0.0722h));
            float2 pixel = input.texcoord * _ScreenParams.xy;
            half dots = Halftone(pixel, luminance);
            half hatch = Hatch(pixel, luminance);

            half dotDarken = dots * _ComicHalftoneIntensity * saturate(1.05h - luminance);
            half hatchDarken = hatch * _ComicHatchIntensity;
            color *= 1.0h - saturate(dotDarken * 0.34h + hatchDarken * 0.30h);

            color += CheapComicBloom(uv, source.rgb);
            color = saturate(color);
            return half4(color, source.a);
        }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 100
        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            Name "Comic Composite"

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment FragComicComposite
            ENDHLSL
        }
    }
}
