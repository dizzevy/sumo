Shader "Sumo/ComicToon"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        _Color("Color", Color) = (1, 1, 1, 1)
        _ShadowColor("Shadow Depth", Color) = (0.34, 0.34, 0.34, 1)
        _InkColor("Ink Color", Color) = (0.015, 0.012, 0.02, 1)
        _InkWidth("Ink Width", Range(0, 8)) = 2.2
        _ShadeSteps("Shade Steps", Range(2, 6)) = 4
        _HalftoneStrength("Halftone Strength", Range(0, 1)) = 0.28
        _HalftoneScale("Halftone Scale", Range(3, 32)) = 9
        _ComicMotionDirection("Comic Motion Direction", Vector) = (0, 0, 1, 0)
        _ComicMotionAmount("Comic Motion Amount", Range(0, 1)) = 0
        _ComicShadePhase("Comic Shade Phase", Float) = 0
        _ComicMotionShadowStrength("Comic Motion Shadow Strength", Range(0, 1)) = 0.1
        _CastShadowPatternStrength("Cast Shadow Pattern Strength", Range(0, 1)) = 0.24
        _CastShadowPatternScale("Cast Shadow Dot Cell Size", Range(0.06, 0.7)) = 0.16
        _CastShadowPosterizeSteps("Cast Shadow Posterize Steps", Range(2, 6)) = 4
        _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.5
        _Surface("__surface", Float) = 0
        _Cull("__cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "SimpleLit"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ComicToon"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ToonVertex
            #pragma fragment ToonFragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _Color;
                half4 _ShadowColor;
                half4 _InkColor;
                half _InkWidth;
                half _ShadeSteps;
                half _HalftoneStrength;
                half _HalftoneScale;
                float4 _ComicMotionDirection;
                half _ComicMotionAmount;
                half _ComicShadePhase;
                half _ComicMotionShadowStrength;
                half _CastShadowPatternStrength;
                half _CastShadowPatternScale;
                half _CastShadowPosterizeSteps;
                half _Cutoff;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ToonVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = NormalizeNormalPerVertex(normalInputs.normalWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                return output;
            }

            half ScreenDot(float2 positionSS, half lightTerm)
            {
                half scale = max(3.0h, _HalftoneScale);
                float2 cell = frac(positionSS / scale) - 0.5;
                half radius = lerp(0.13h, 0.39h, saturate(1.0h - lightTerm));
                return 1.0h - step(length(cell), radius) * _HalftoneStrength * saturate(1.0h - lightTerm);
            }

            half Luminance(half3 color)
            {
                return dot(color, half3(0.2126h, 0.7152h, 0.0722h));
            }

            half ComicBand(half value, half steps)
            {
                return saturate(floor(saturate(value) * steps) / max(1.0h, steps - 1.0h));
            }

            half3 SafeNormal(half3 value, half3 fallback)
            {
                return dot(value, value) > 0.0001h ? normalize(value) : fallback;
            }

            half MotionShadowBias(half3 normalWS)
            {
                half motion = saturate(_ComicMotionAmount * _ComicMotionShadowStrength);
                half3 motionDirection = SafeNormal(_ComicMotionDirection.xyz, half3(0.0h, 0.0h, 1.0h));
                return dot(normalWS, motionDirection) * motion * 0.08h;
            }

            half CastShadowPattern(float3 positionWS, half shadowTerm)
            {
                half shadowMask = saturate(1.0h - shadowTerm);
                float cellSize = max(0.04, _CastShadowPatternScale);
                float2 cell = frac(positionWS.xz / cellSize) - 0.5;
                float dotDistance = length(cell);
                float dotAA = max(fwidth(dotDistance) * 1.35, 0.006);
                half dots = 1.0h - smoothstep(0.115h - dotAA, 0.115h + dotAA, dotDistance);
                half hatch = 1.0h - smoothstep(0.022h, 0.034h, abs(frac((positionWS.x + positionWS.z) / (cellSize * 2.2)) - 0.5h));
                return saturate((dots * 0.88h + hatch * 0.12h) * shadowMask * _CastShadowPatternStrength);
            }

            half4 ToonFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 tint = _BaseColor;
                half4 baseColor = tex * tint;
                clip(baseColor.a - _Cutoff);

                half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half ndotl = saturate(dot(normalWS, mainLight.direction) + MotionShadowBias(normalWS));
                half steps = max(2.0h, _ShadeSteps);
                half band = ComicBand(ndotl, steps);
                half shadowBand = ComicBand(mainLight.shadowAttenuation, max(2.0h, _CastShadowPosterizeSteps));
                half mainTerm = ComicBand(saturate(0.12h + band * shadowBand * mainLight.distanceAttenuation), steps);

                half shadowDepth = saturate(1.0h - Luminance(_ShadowColor.rgb));
                half shade = lerp(1.0h - shadowDepth * 0.86h, 1.0h, mainTerm);
                shade *= 1.0h - CastShadowPattern(input.positionWS, shadowBand) * 0.38h;
                half lightBrightness = max(0.04h, Luminance(mainLight.color));
                half3 color = baseColor.rgb * shade * lightBrightness;

                #if defined(_ADDITIONAL_LIGHTS)
                uint lightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(lightCount)
                    Light light = GetAdditionalLight(lightIndex, input.positionWS);
                    half addNdotL = saturate(dot(normalWS, light.direction));
                    half addBand = ComicBand(addNdotL, steps);
                    half addBrightness = Luminance(light.color);
                    color += baseColor.rgb * addBrightness * addBand * light.distanceAttenuation * light.shadowAttenuation * 0.35h;
                LIGHT_LOOP_END
                #endif

                color *= ScreenDot(input.positionCS.xy, mainTerm);
                color = MixFog(color, input.fogFactor);
                return half4(saturate(color), baseColor.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ComicOutline"
            Tags { "LightMode" = "ComicOutline" }

            Cull Front
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex OutlineVertex
            #pragma fragment OutlineFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _Color;
                half4 _ShadowColor;
                half4 _InkColor;
                half _InkWidth;
                half _ShadeSteps;
                half _HalftoneStrength;
                half _HalftoneScale;
                float4 _ComicMotionDirection;
                half _ComicMotionAmount;
                half _ComicShadePhase;
                half _ComicMotionShadowStrength;
                half _CastShadowPatternStrength;
                half _CastShadowPatternScale;
                half _CastShadowPosterizeSteps;
                half _Cutoff;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings OutlineVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);
                float outlineWidth = _InkWidth * 0.0065;
                float3 positionWS = positionInputs.positionWS + normalize(normalInputs.normalWS) * outlineWidth;
                output.positionCS = TransformWorldToHClip(positionWS);
                return output;
            }

            half4 OutlineFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return _InkColor;
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        UsePass "Universal Render Pipeline/Lit/DepthNormals"
    }

    FallBack "Universal Render Pipeline/Lit"
}
