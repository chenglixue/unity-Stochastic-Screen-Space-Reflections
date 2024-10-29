Shader "Elysia/SSR"
{
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
        #pragma target 4.5
        #pragma enable_d3d11_debug_symbols
        #include_with_pragmas "SSR.hlsl"

        PSInput VS(VSInput i)
        {
            PSInput o = (PSInput)0;

            o.positionCS = mul(UNITY_MATRIX_MVP, float4(i.positionOS, 1.f));

            o.uv = i.uv;

            return o;
        }
        ENDHLSL
        
        // 0
        Pass
        {
            Name "Copy Depth"
            HLSLPROGRAM
            #pragma vertex VS
            #pragma fragment CopyDepth

            void CopyDepth(PSInput i, out PSOutput o)
            {
                o.color.r = GetDeviceDepth(i.uv);
            }
            ENDHLSL
        }
        
        // 1
        Pass
        {
            Name "Linear SSR"
            
            HLSLPROGRAM
            #pragma vertex VS
            #pragma fragment SSRScreenSpacePS
            #include "Assets/Materials/BRDF.hlsl"
            #include "Assets/Materials/Common.hlsl"
            
            #pragma multi_compile _ _GBUFFER_NORMALS_OCT

            void SSRScreenSpacePS(PSInput i, out float4 color : SV_Target0, out float mask : SV_Target1)
            {
                color = 0.f;
                mask = 0.f;
                float2 screenUV   = i.positionCS.xy * _ViewSize.zw;
                float rawDepth = GetDeviceDepth(screenUV);
                if(rawDepth == 0.f)
                {
                    color = 0.f;
                    mask = 0.f;
                    return;
                }
                float smoothness = GetSmoothness(screenUV);
                float roughness = GetRoughness(smoothness);
                
                float4 posCS       = GetPositionNDC(screenUV, rawDepth);
                float4 posVS       = GetPositionVS(posCS, Matrix_I_P);
                float4 posWS       = GetPositionWS(posVS, Matrix_I_V);
                float3 normalWS    = GetNormalWS(screenUV);
                float3 invViewDir  = normalize(posWS - _WorldSpaceCameraPos);

                float2 ditherUV = fmod(i.positionCS.xy, 4);
                float2 jitter = _BlueNoiseTex.SampleLevel(Smp_ClampU_ClampV_Linear, ditherUV / 4.f + float2(0.5 / 4.f, 0.5f / 4.f), 0).xy;
                jitter.y = lerp(jitter.y, 0.f, _BRDFBias);

                float4 halfVector = 0.f;
                if(roughness > 0.1f)
                {
                    halfVector = TransformTangentToView(normalWS, ImportanceSampleGGX(jitter, roughness));
                }
                else
                {
                    halfVector = float4(normalWS, 100);
                }
                float3 reflectDir = reflect(invViewDir, halfVector.xyz);
                
                Ray ray;
                ray.positionWS  = posWS;
                ray.positionVS  = posVS;
                ray.directionWS = reflectDir;
                ray.directionVS = SafeNormalize(mul(Matrix_V, float4(ray.directionWS, 0.f)));
                
                if(smoothness >= _MinSmoothness)
                {
                    float4 hitData;
                    RayMarching(ray, hitData);
                    color = float4(hitData.xyz, halfVector.w);
                    mask = hitData.w;
                }
            }
            ENDHLSL
        }

        // 2
        Pass
        {
            Name"Debug Hit UV"
            HLSLPROGRAM
            #pragma vertex VS
            #pragma fragment DebugHitUV

            void DebugHitUV(PSInput i, out PSOutput o)
            {
                float2 uv = i.uv;
                o.color.rg = _SSRHitData.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0).rg;
            }
            ENDHLSL
        }

        // 3
        Pass
        {
            Name"Debug Hit Depth"
            HLSLPROGRAM
            #pragma vertex VS
            #pragma fragment DebugHitDepth

            void DebugHitDepth(PSInput i, out PSOutput o)
            {
                float2 uv = i.uv;
                o.color.r = _SSRHitData.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0).b;
            }
            ENDHLSL
        }

        // 4
        Pass
        {
            Name"Debug Hit Mask"
            HLSLPROGRAM
            #pragma vertex VS
            #pragma fragment DebugHitMask

            void DebugHitMask(PSInput i, out PSOutput o)
            {
                float2 uv = i.uv;
                o.color.r = _SSRHitMask.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0).r;
            }
            ENDHLSL
        }

        // 5
        Pass
        {
            Name "Resolved"
            
            HLSLPROGRAM
            #pragma vertex VS
            #pragma fragment Resolved
            #include "Assets/Materials/BRDF.hlsl"

            void Resolved(PSInput i, out PSOutput o)
            {
                float2 uv = i.uv;

                float   smoothness  = GetSmoothness(uv);
                if(smoothness < _MinSmoothness)
                {
                    o.color = 0;
                    return;
                }
                float   roughness   = GetRoughness(smoothness);
                float   rawDepth    = GetDeviceDepth(uv);
                if(rawDepth == 0.f)
                {
                    o.color = 0.f;
                    return;
                }
                
                float3  normalWS    = GetNormalWS(uv);
                float3  normalVS    = GetNormalVS(normalWS);

                float4 positionCS   = GetPositionNDC(uv, rawDepth);
                float4 positionVS   = GetPositionVS(positionCS, Matrix_I_P);

                float2 ditherUV     = fmod(i.positionCS.xy, 4);
                float2 jitter       = _BlueNoiseTex.SampleLevel(Smp_ClampU_ClampV_Linear, ditherUV / 4.f + float2(0.5 / 4.f, 0.5f / 4.f), 0).xy;
                float2x2 offsetRotationMat = float2x2(jitter.x, jitter.y, -jitter.y, -jitter.x);

                float  weightSum    = 0.f;
                float4 result       = 0.f;
                for(int i = 0; i < 4; ++i)
                {
                    float2 offsetUV         = mul(offsetRotationMat, offset[i] * _ResolvedTexSize.zw);
                    float2 neighborUV       = uv + offsetUV;

                    float4 hitData          = _SSRHitData.SampleLevel(Smp_ClampU_ClampV_Linear, neighborUV, 0);
                    float2 hitUV            = hitData.rg;
                    float  hitZ             = hitData.b;
                    float  pdf              = hitData.a;
                    float4 hitPositionCS    = GetPositionNDC(hitUV, hitZ);
                    float4 hitPositionVS    = GetPositionVS(hitPositionCS, Matrix_I_P);

                    float weight            = BRDF_UE4(-positionVS, normalize(hitPositionVS - positionVS), normalVS, roughness) / max(1e-5, pdf);
                    float4 currColor        = 0.f;
                    currColor.rgb           = GetSourceColor(hitUV).rgb;
                    currColor.a             = _SSRHitMask.SampleLevel(Smp_ClampU_ClampV_Linear, neighborUV, 0).r;

                    result      += currColor * weight;
                    weightSum   += weight;
                }

                result /= weightSum;
                
                o.color = result;
            }
            ENDHLSL
        }

        // 6
        Pass
        {
            Name "Temporalfilter"
            
            HLSLPROGRAM
            #pragma vertex VS
            #pragma fragment Temporalfilter
            #include "Assets/Materials/Filtter.hlsl"

            void Temporalfilter(PSInput i, out PSOutput o)
            {
                float2 uv = i.uv;
                float  hitDepth = _SSRHitData.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0).b;
                float roughness = GetRoughness(GetSmoothness(uv));
                float3 normalWS = GetNormalWS(uv);

                float2 depthVelocity  = _MotionVectorTexture.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0).rg;
                float2 rayVelocity    = GetCameraMotionVector(1 - hitDepth, uv, Matrix_I_VP, _Pre_Matrix_VP, Matrix_VP);
                float Velocity_Weight = saturate(dot(normalWS, float3(0, 1, 0)));
                float2 velocity = lerp(depthVelocity, rayVelocity, Velocity_Weight);

                float2 du = float2(_TAATexSize.z, 0);
                float2 dv = float2(0, _TAATexSize.w);

                float4 minColor = 1e10, maxColor = 0;
                for(int i = -1; i <= 1; ++i)
                {
                    for(int j = -1; j <= 1; ++j)
                    {
                        float4 currColor = _ResolvedTex.SampleLevel(Smp_ClampU_ClampV_Linear, uv + du * i + dv * j, 0);
                        minColor = min(minColor, currColor);
                        maxColor = max(maxColor, currColor);
                    }
                }
                float4 averageColor = (minColor + maxColor) * 0.5f;
                minColor = (minColor - averageColor) * _TAAScale + averageColor;
                maxColor = (maxColor - averageColor) * _TAAScale + averageColor;
                
                float4 TAAPreColor = _TAAPreTex.SampleLevel(Smp_ClampU_ClampV_Linear, uv - velocity, 0);
                TAAPreColor = clamp(TAAPreColor, minColor, maxColor);
                float4 TAACurrColor = _ResolvedTex.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0);

                float TAAWeight = _TAAWeight;
                if(roughness > 0.1)
                {
                    TAAWeight = _TAAWeight;
                }
                else
                {
                    TAAWeight = 0.92f;
                }

                float weight = saturate(clamp(0, 0.96f, TAAWeight) * (1.f - length(velocity) * 8));
                float4 reflectColor = lerp(TAACurrColor, TAAPreColor, weight);

                o.color = reflectColor;
            }
            ENDHLSL
        }

        // 7
        Pass
        {
            Name "SSR Combine"
            HLSLPROGRAM
            #pragma vertex VS
            #pragma fragment Combine
            
            void Combine(PSInput i, out PSOutput o)
            {
                float2 uv = i.uv;

                float rawDepth = GetDeviceDepth(uv);
                if(rawDepth == 0.f)
                {
                    o.color = GetSourceColor(uv);
                    return;
                }

                float mask = _SSRHitMask.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0).r;
                float4 reflectColor = _TAACurrTex.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0);
                float4 sourceTex = _CameraColorTexture.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0) * (1 - mask);
                
                o.color = sourceTex + reflectColor;
            }
            ENDHLSL
        }
    }
}
