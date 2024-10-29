#pragma once
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

SamplerState Smp_ClampU_ClampV_Linear;
SamplerState Smp_ClampU_RepeatV_Linear;
SamplerState Smp_RepeatU_RepeatV_Linear;
SamplerState Smp_RepeatU_ClampV_Linear;
SamplerState Smp_ClampU_ClampV_Point;
SamplerState Smp_ClampU_RepeatV_Point;
SamplerState Smp_RepeatU_RepeatV_Point;
SamplerState Smp_RepeatU_ClampV_Point;

#define INFINITY 1e10
#define kDieletricSpec half4(0.04, 0.04, 0.04, 1.0 - 0.04)

float Square(float x)
{
    return x * x;
}

float2 Square(float2 x)
{
    return x * x;
}

float3 Square(float3 x)
{
    return x * x;
}

float4 Square(float4 x)
{
    return x * x;
}

float pow2(float x)
{
    return x * x;
}

float2 pow2(float2 x)
{
    return x * x;
}

float3 pow2(float3 x)
{
    return x * x;
}

float4 pow2(float4 x)
{
    return x * x;
}

float pow3(float x)
{
    return x * x * x;
}

float2 pow3(float2 x)
{
    return x * x * x;
}

float3 pow3(float3 x)
{
    return x * x * x;
}

float4 pow3(float4 x)
{
    return x * x * x;
}

float pow4(float x)
{
    float xx = x * x;
    return xx * xx;
}

float2 pow4(float2 x)
{
    float2 xx = x * x;
    return xx * xx;
}

float3 pow4(float3 x)
{
    float3 xx = x * x;
    return xx * xx;
}

float4 pow4(float4 x)
{
    float4 xx = x * x;
    return xx * xx;
}

float pow5(float x)
{
    float xx = x * x;
    return xx * xx * x;
}

float2 pow5(float2 x)
{
    float2 xx = x * x;
    return xx * xx * x;
}

float3 pow5(float3 x)
{
    float3 xx = x * x;
    return xx * xx * x;
}

float4 pow5(float4 x)
{
    float4 xx = x * x;
    return xx * xx * x;
}

float pow6(float x)
{
    float xx = x * x;
    return xx * xx * xx;
}

float2 pow6(float2 x)
{
    float2 xx = x * x;
    return xx * xx * xx;
}

float3 pow6(float3 x)
{
    float3 xx = x * x;
    return xx * xx * xx;
}

float4 pow6(float4 x)
{
    float4 xx = x * x;
    return xx * xx * xx;
}
inline half min3(half a, half b, half c)
{
    return min(min(a, b), c);
}

inline half max3(half a, half b, half c)
{
    return max(a, max(b, c));
}

inline half4 min3(half4 a, half4 b, half4 c)
{
    return half4(
        min3(a.x, b.x, c.x),
        min3(a.y, b.y, c.y),
        min3(a.z, b.z, c.z),
        min3(a.w, b.w, c.w));
}

inline half4 max3(half4 a, half4 b, half4 c)
{
    return half4(
        max3(a.x, b.x, c.x),
        max3(a.y, b.y, c.y),
        max3(a.z, b.z, c.z),
        max3(a.w, b.w, c.w));
}

inline half acosFast(half inX)
{
    half x = abs(inX);
    half res = -0.156583f * x + (0.5 * PI);
    res *= sqrt(1 - x);
    return (inX >= 0) ? res : PI - res;
}

inline half asinFast(half x)
{
    return (0.5 * PI) - acosFast(x);
}

float Luma4(float3 Color)
{
    return (Color.g * 2.0) + (Color.r + Color.b);
}

float Luma(float3 Color)
{
    return (0.2126 * Color.r) + (0.7152 * Color.g) + (0.0722 * Color.b);
}

/// 计算权重值，用于调整颜色亮度以适应HDR显示
//  Exposure : 调整亮度计算结果
inline half HDRWeight4(half3 Color, half Exposure)
{
    return rcp(Luma4(Color) * Exposure + 4);
}

inline float4 GetPositionNDC(float2 uv, float rawDepth)
{
    return float4(uv * 2 - 1, rawDepth, 1.f);
}

inline float4 GetPositionVS(float4 positionNDC, float4x4 Matrix_I_P)
{
    float4 positionVS = mul(Matrix_I_P, positionNDC);
    positionVS /= positionVS.w;
    #if defined (UNITY_UV_STARTS_AT_TOP)
    positionVS.y *= -1;
    #endif

    return positionVS;
}

inline float4 GetPositionWS(float4 positionVS, float4x4 Matrix_I_V)
{
    return mul(Matrix_I_V, positionVS);
}

inline float4 TransformNDCToWS(float4 positionNDC, float4x4 Matrix_I_VP)
{
    float4 positionWS = mul(Matrix_I_VP, positionNDC);
    positionWS /= positionWS.w;
    #if defined (UNITY_UV_STARTS_AT_TOP)
    positionWS.y *= -1;
    #endif

    return positionWS;
}