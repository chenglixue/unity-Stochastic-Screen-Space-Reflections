#pragma once
#include "Assets/Materials/Common.hlsl"

cbuffer PS_PROPERTIES_BUFFER
{
    float4  _ViewSize;
    float4  _BlueNoiseTexSize;
    float4  _ResolvedTexSize;
    float4  _TAATexSize;
    int    _MaxStep;
    int    _BinaryCount;
    float  _MaxDistance;
    float  _Thickness;
    float  _MinSmoothness;
    float  _BRDFBias;
    float  _TAAScale;
    float  _TAAWeight;
    static const int2 offset[9] =
    {
        int2(-2.0, -2.0),
        int2(0.0, -2.0),
        int2(2.0, -2.0),
        int2(-2.0, 0.0),
        int2(0.0, 0.0),
        int2(2.0, 0.0),
        int2(-2.0, 2.0),
        int2(0.0, 2.0),
        int2(2.0, 2.0)
    };   
}

float4x4 Matrix_V;
float4x4 Matrix_I_V;
float4x4 Matrix_P;
float4x4 Matrix_I_P;
float4x4 Matrix_VP;
float4x4 Matrix_I_VP;
float4x4 _Pre_Matrix_VP;

Texture2D<float4> _GBuffer0;
Texture2D<float4> _GBuffer1;
Texture2D<float4> _GBuffer2;
Texture2D<float4> _CameraColorTexture;
Texture2D<float>  _CameraDepthTexture;
Texture2D<float2> _MotionVectorTexture;

Texture2D<float3> _BlueNoiseTex;
Texture2D<float>  _HiZDepthTex0;
Texture2D<float4> _SSRHitData;
Texture2D<float>  _SSRHitMask;
Texture2D<float2> _HitUVTex;
Texture2D<float>  _HitZTex;
Texture2D<float>  _HitMaskTex;
Texture2D<float4> _ResolvedTex;
Texture2D<float4> _TAACurrTex;
Texture2D<float4> _TAAPreTex;

struct VSInput
{
    float3 positionOS : POSITION;
    float2 uv         : TEXCOORD0;
};
struct PSInput
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
};
struct PSOutput
{
    float4 color : SV_Target;
};

struct Ray
{
    float3 positionWS;      // ray start pos in world space
    float3 directionWS;     // ray direction in world space
    float3 positionVS;
    float3 directionVS;
};

float4 GetSourceColor(float2 uv)
{
    return _CameraColorTexture.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0);
}
float3 GetAlbedo(float2 uv)
{
    return _GBuffer0.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0).rgb;
}
float GetSmoothness(float2 uv)
{
    return _GBuffer2.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0).a;
}
float GetRoughness(float smoothness)
{
    smoothness = max(smoothness, _MinSmoothness);
    float roughness     = clamp(1 - smoothness, 0.02f, 1.f);

    return roughness;
}
float GetAO(float2 uv)
{
    return _GBuffer1.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0).a;
}

float GetThicknessDiff(float depthDiff, float linearSampleDepth)
{
    return depthDiff / linearSampleDepth;
}
float GetDeviceDepth(float2 uv)
{
    return _CameraDepthTexture.SampleLevel(Smp_ClampU_ClampV_Point, uv, 0).r;
}
float GetLinearEyeDepth(float rawDepth)
{
    return LinearEyeDepth(rawDepth, _ZBufferParams);
}
float GetHizDepth(float2 uv, int mipLevel)
{
    return _HiZDepthTex0.SampleLevel(Smp_ClampU_ClampV_Linear, uv, mipLevel).r;
}

float3 GetNormalWS(float2 uv)
{
    float3 normal = _GBuffer2.SampleLevel(Smp_ClampU_ClampV_Linear, uv, 0).xyz;
    #if defined(_GBUFFER_NORMALS_OCT)
    float2 remappedOctNormalWS = Unpack888ToFloat2(normal);
    float2 octNormalWS = remappedOctNormalWS.xy * 2.0 - 1.0;
    normal = UnpackNormalOctQuadEncode(octNormalWS);
    #else
    normal = SafeNormalize(normal);
    #endif

    return normal;
}
float3 GetNormalVS(float3 normalWS)
{
    float3 normalVS = mul(Matrix_V, float4(normalWS, 0.f));
    normalVS = SafeNormalize(normalVS);

    return normalVS;
}
float3 GetViewDir(float3 positionWS)
{
    return normalize(positionWS - _WorldSpaceCameraPos);
}
float3 GerReflectDirWS(float3 invViewDir, float3 normalWS)
{
    float3 reflectDir = reflect(invViewDir, normalWS);
    reflectDir = normalize(reflectDir);

    return reflectDir;
}

void RayMarching(Ray ray, out float4 hitData)
{
    float maxDistance = ray.positionVS.z + ray.directionVS.z * _MaxDistance > -_ProjectionParams.y ?
                    (-_ProjectionParams.y - ray.positionVS.z) / ray.directionVS.z : _MaxDistance;
    float stepSize  = rcp(float(_MaxStep));

    float3 startPosVS   = ray.positionVS;
    float3 endPosVS     = startPosVS + ray.directionVS * maxDistance;
    float4 startPosCS = mul(Matrix_P, float4(startPosVS, 1.f));
    float4 endPosCS   = mul(Matrix_P, float4(endPosVS, 1.f));
    float  startK     = rcp(startPosCS.w);
    float  endK       = rcp(endPosCS.w);
    float2 startUV    = startPosCS.xy * float2(1.f, -1.f) * startK * 0.5f + 0.5f;
    float2 endUV      = endPosCS.xy * float2(1.f, -1.f) * endK * 0.5f + 0.5f;

    float w0 = 0.f, w1 = 0.f;
    float mask = 0.f;
    int mipLevel = 0;
    float2 resultUV = startUV;
    float resultDepth = startPosCS.z * startK;
    [unroll(64)]
    for(int i = 0; i < _MaxStep; ++i)
    {
        w1  = w0;
        w0 += stepSize;

        float  reflectK     = lerp(startK, endK, w0);
        float2 reflectUV    = lerp(startUV, endUV, w0);
        float4 reflectPosCS = lerp(startPosCS, endPosCS, w0);

        if(reflectUV.x < 0.f || reflectUV.y < 0.f || reflectUV.x > 1.f || reflectUV.y > 1.f) break;

        float sceneDepth    = GetHizDepth(reflectUV, mipLevel).r;
        sceneDepth          = GetLinearEyeDepth(sceneDepth);
        float rayDepth      = GetLinearEyeDepth(reflectPosCS.z * reflectK);

        if(sceneDepth > rayDepth)
        {
            mipLevel = min(mipLevel + 1, 6);
            resultUV = reflectUV;
            resultDepth = reflectPosCS.z * reflectK;
        }
        else
        {
            mipLevel--;
        }

        if(mipLevel < 0)
        {
            float depthDiff     = rayDepth - sceneDepth;
            float thicknessDiff = GetThicknessDiff(depthDiff, sceneDepth);
            mask = depthDiff > 0.f && thicknessDiff < _Thickness;
            break;
        }
    }
    
    hitData = float4(resultUV, resultDepth, mask);
}