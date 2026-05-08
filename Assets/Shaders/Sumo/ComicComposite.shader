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
        float _ComicScreenOutlineStrength;
        float _ComicScreenOutlineThickness;
        float _ComicScreenOutlineDepthSensitivity;
        float _ComicScreenOutlineNormalSensitivity;
        float _ComicPaintPatchStrength;
        float _ComicLiteMode;

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

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

        half Hash21(float2 p)
        {
            p = frac(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return frac(p.x * p.y);
        }

        half ValueNoise(float2 p)
        {
            float2 cell = floor(p);
            float2 local = frac(p);
            float2 blend = local * local * (3.0 - 2.0 * local);

            half a = Hash21(cell);
            half b = Hash21(cell + float2(1.0, 0.0));
            half c = Hash21(cell + float2(0.0, 1.0));
            half d = Hash21(cell + float2(1.0, 1.0));
            return lerp(lerp(a, b, blend.x), lerp(c, d, blend.x), blend.y);
        }

        half PainterlyPatch(float2 uv, half luminance)
        {
            float2 aspectUv = float2(uv.x * (_ScreenParams.x / max(1.0, _ScreenParams.y)), uv.y);
            half broad = ValueNoise(aspectUv * 7.0 + luminance * 1.37);
            half medium = ValueNoise(aspectUv * 13.0 + 4.7);
            half patch = saturate(broad * 0.74h + medium * 0.26h);
            return floor(patch * 4.0h) / 3.0h;
        }

        half3 ApplyPainterlyColor(half3 color, float2 uv)
        {
            half strength = saturate(_ComicPaintPatchStrength);
            if (strength <= 0.0001h)
            {
                return color;
            }

            half luminance = dot(color, half3(0.2126h, 0.7152h, 0.0722h));
            half patch = PainterlyPatch(uv, luminance);
            half warmth = PainterlyPatch(uv + float2(0.19, 0.41), 1.0h - luminance);
            half shade = lerp(0.90h, 1.10h, patch);
            half3 colorBias = lerp(half3(0.90h, 1.02h, 0.96h), half3(1.08h, 1.03h, 0.90h), warmth);
            half3 painted = saturate(color * shade * colorBias);
            return lerp(color, painted, strength);
        }

        half Luma(half3 color)
        {
            return dot(color, half3(0.2126h, 0.7152h, 0.0722h));
        }

        float RelativeDepthDelta(float2 uv, float centerDepth)
        {
            float sampleDepth = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
            return abs(sampleDepth - centerDepth) / max(0.5, centerDepth);
        }

        half OutlineEdgeAt(float2 sampleUv, float centerDepth, half3 centerNormal, half centerLuma)
        {
            sampleUv = saturate(sampleUv);
            half3 sampleColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, sampleUv).rgb;
            half3 sampleNormal = SampleSceneNormals(sampleUv);
            half depthEdge = saturate(RelativeDepthDelta(sampleUv, centerDepth) * _ComicScreenOutlineDepthSensitivity);
            half normalEdge = saturate(length(centerNormal - sampleNormal) * _ComicScreenOutlineNormalSensitivity);
            half colorEdge = saturate(abs(centerLuma - Luma(sampleColor)) * 2.2h);
            return max(depthEdge, max(normalEdge, colorEdge));
        }

        half ScreenOutline(float2 uv, half3 color)
        {
            half strength = saturate(_ComicScreenOutlineStrength * _ComicOutlineStrength);
            if (strength <= 0.0001h)
            {
                return 0.0h;
            }

            float thickness = max(0.5, _ComicScreenOutlineThickness);
            float2 texel = _BlitTexture_TexelSize.xy * thickness;
            float centerDepth = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
            half3 centerNormal = SampleSceneNormals(uv);
            half centerLuma = Luma(color);

            half edge = 0.0h;
            edge = max(edge, OutlineEdgeAt(uv + float2(texel.x, 0.0), centerDepth, centerNormal, centerLuma));
            edge = max(edge, OutlineEdgeAt(uv + float2(-texel.x, 0.0), centerDepth, centerNormal, centerLuma));
            edge = max(edge, OutlineEdgeAt(uv + float2(0.0, texel.y), centerDepth, centerNormal, centerLuma));
            edge = max(edge, OutlineEdgeAt(uv + float2(0.0, -texel.y), centerDepth, centerNormal, centerLuma));

            return smoothstep(0.08h, 0.24h, edge) * strength;
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

            color = ApplyPainterlyColor(color, uv);

            color += CheapComicBloom(uv, source.rgb);
            half outline = ScreenOutline(uv, source.rgb);
            color = lerp(color, half3(0.012h, 0.010h, 0.018h), saturate(outline));
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
