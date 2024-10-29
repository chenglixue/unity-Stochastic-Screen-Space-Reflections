#pragma once

#define PI          3.14159265358979323846

float4 TransformTangentToView(float3 normal, float4 H)
{
    float3 upDir        = abs(normal.z) < 0.999f ? float3(0.f, 0.f, 1.f) : float3(1.f, 0.f, 0.f);
    float3 tangent      = normalize(cross(upDir, normal));
    float  bitTangent   = cross(normal, tangent);

    return float4(tangent * H.x + bitTangent * H.y + normal * H.z, H.w);
}

float4 ImportanceSampleGGX(float2 Xi, float Roughness)
{
    float m = Roughness * Roughness;
    float m2 = m * m;
    
    float Phi = 2 * PI * Xi.x;
	
    float CosTheta = sqrt((1.0 - Xi.y) / (1.0 + (m2 - 1.0) * Xi.y));
    float SinTheta = sqrt(max(1e-5, 1.0 - CosTheta * CosTheta));

    // 半程向量(采样方向)
    float3 H;
    H.x = SinTheta * cos(Phi);
    H.y = SinTheta * sin(Phi);
    H.z = CosTheta;
    
    float d = (CosTheta * m2 - CosTheta) * CosTheta + 1;
    float D = m2 / (PI * d * d);
    float pdf = D * CosTheta;

    return float4(H, pdf);
}

float NDF_GGX(float Roughness, float NdotH)
{
    float m = Roughness * Roughness;
    float m2 = m * m;
	
    float D = m2 / (PI * sqrt(sqrt(NdotH) * (m2 - 1) + 1));
	
    return D;
}

float G_GGX(float Roughness, float NdotL, float NdotV)
{
    float m = Roughness * Roughness;
    float m2 = m * m;

    float G_L = 1.0f / (NdotL + sqrt(m2 + (1 - m2) * NdotL * NdotL));
    float G_V = 1.0f / (NdotV + sqrt(m2 + (1 - m2) * NdotV * NdotV));
    float G = G_L * G_V;
	
    return G;
}

float BRDF_UE4(float3 V, float3 L, float3 N, float Roughness)
{
    float3 H = normalize(L + V);

    float NdotH = saturate(dot(N,H));
    float NdotL = saturate(dot(N,L));
    float NdotV = saturate(dot(N,V));

    float D = NDF_GGX(Roughness, NdotH);
    float G = G_GGX(Roughness, NdotL, NdotV);

    return D * G;
}