#ifndef VOLUMETRICFOGUTILS
#define VOLUMETRICFOGUTILS

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/BRDF.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GlobalIllumination.hlsl"

float4 _BlitTexture_TexelSize;

#if defined(USING_STEREO_MATRICES)
    #define unity_eyeIndex unity_StereoEyeIndex
#else
    #define unity_eyeIndex 0
#endif

float4 _CameraViewTopLeftCorner[2];
float4x4 _CameraViewProjections[2]; // This is different from UNITY_MATRIX_VP (platform-agnostic projection matrix is used). Handle both non-XR and XR modes.

float4 _SourceSize;
float4 _ProjectionParams2;
float4 _CameraViewXExtent[2];
float4 _CameraViewYExtent[2];
float4 _CameraViewZExtent[2];

half3 ReconstructViewPos(float2 uv, float linearDepth)
{
    // Screen is y-inverted.
    uv.y = 1.0 - uv.y;

    // view pos in world space
    float zScale = linearDepth * _ProjectionParams2.x; // divide by near plane
    float3 viewPos = _CameraViewTopLeftCorner[unity_eyeIndex].xyz
                            + _CameraViewXExtent[unity_eyeIndex].xyz * uv.x
                            + _CameraViewYExtent[unity_eyeIndex].xyz * uv.y;
    viewPos *= zScale;

    return half3(viewPos);
}

// Globals
int _VFogEnabled;
float _VFogDensity;
float4 _VFogColor;
float _VFogScattering;
float _VFogExtinction;
float _VFogAnisotropy;
float _VFogSkyDistance;
float _VFogHeightFogDensity;
float _VFogHeightBase;
float _VFogHeightTransitionSize;
float _VFogDirShadowExtinction;
float _VFogAddShadowExtinction;
float _VFogDirShadowDensity;
float _VFogAddShadowDensity;
float _VFogDirShadowDistance;
float _VFogAddShadowDistance;

uniform float _RaymarchSteps;
uniform float _MaxDistance;
uniform float _LowestMipDistance;
uniform float _LowestMip;
uniform int _DistantMarch;

TEXTURE2D(_VFogBlueNoise);
TEXTURE2D_X(_VFogBuffer);
TEXTURE2D_X(_BlurBuffer);
TEXTURE2D_X(_HalfDepthBuffer);
float4 _HalfDepthBuffer_TexelSize;
float4 _VFogBuffer_TexelSize;

float BlueNoise(float2 uv)
{
    float2 coords = (uv * _ScreenParams.xy) / 256.0;
    return SAMPLE_TEXTURE2D(_VFogBlueNoise, sampler_PointRepeat, coords).r;
}

float GetHalfDepth(float2 uv)
{
    float d = SAMPLE_TEXTURE2D_X(_HalfDepthBuffer, sampler_PointClamp, uv).r;
    return LinearEyeDepth(d, _ZBufferParams);
}

float GetLinearDepth(float2 uv)
{
    float d = SampleSceneDepth(uv);
    return LinearEyeDepth(d, _ZBufferParams);
}

float4 LinearEyeDepth(float4 d)
{
    float4 ld = 0;
    ld.x = LinearEyeDepth(d.x, _ZBufferParams);
    ld.y = LinearEyeDepth(d.y, _ZBufferParams);
    ld.z = LinearEyeDepth(d.z, _ZBufferParams);
    ld.w = LinearEyeDepth(d.w, _ZBufferParams);
    return ld;
}

float getCornetteShanks(float costh)
{
    float g2 = _VFogAnisotropy * _VFogAnisotropy;
			     
    return (3.0 * (1.0 - g2) * (1.0 + costh * costh)) / (4.0 * PI * 2.0 * (2.0 + g2) * pow(1.0 + g2 - 2.0 * _VFogAnisotropy * costh, 3.0/2.0));
}

// Henyey-Greenstein Phase Function
float hg(float costh)
{
    return getCornetteShanks(costh);
    float g = _VFogAnisotropy;
    return (1.0 - g * g) / (4.0 * PI * pow(1.0 + g * g - 2.0 * g * costh, 3.0/2.0));
}

float invLerp(float from, float to, float value)
{
    return (value - from) / (to - from);
}

float4 invLerp(float4 from, float4 to, float4 value)
{
    return (value - from) / (to - from);
}

float remap(float origFrom, float origTo, float targetFrom, float targetTo, float value)
{
    float rel = invLerp(origFrom, origTo, value);
    return lerp(targetFrom, targetTo, rel);
}

float4 remap(float4 origFrom, float4 origTo, float4 targetFrom, float4 targetTo, float4 value)
{
    float4 rel = invLerp(origFrom, origTo, value);
    return lerp(targetFrom, targetTo, rel);
}

float GetAmbientMip(float distance)
{
    return clamp(invLerp(_LowestMipDistance, 0, distance) * 8,_LowestMip,8);
}

void UpdateNearestSample(	inout float MinDist,
								inout float2 NearestUV,
								float Z,
								float2 UV,
								float ZFull
								)
	{
		float Dist = abs(Z - ZFull);
		if (Dist < MinDist)
		{
			MinDist = Dist;
			NearestUV = UV;
		}
	}

float4 GetNearestColor(float2 uv)
{
    float ZFull = GetLinearDepth(uv);

    const float depthThreshold = 0.5;
    float MinDist = 1.e8f;

    float2 lowResUV = uv;
    float2 lowResTexelSize = _HalfDepthBuffer_TexelSize.xy;

    float2 UV00 = lowResUV - 0.5 * lowResTexelSize;
    float2 NearestUV = UV00;
    float Z00 = GetHalfDepth(UV00);   
    UpdateNearestSample(MinDist, NearestUV, Z00, UV00, ZFull);
    
    float2 UV10 = float2(UV00.x+lowResTexelSize.x, UV00.y);
    float Z10 = GetHalfDepth(UV10);  
    UpdateNearestSample(MinDist, NearestUV, Z10, UV10, ZFull);
    
    float2 UV01 = float2(UV00.x, UV00.y+lowResTexelSize.y);
    float Z01 = GetHalfDepth(UV01);  
    UpdateNearestSample(MinDist, NearestUV, Z01, UV01, ZFull);
    
    float2 UV11 = UV00 + lowResTexelSize;
    float Z11 = GetHalfDepth(UV11);  
    UpdateNearestSample(MinDist, NearestUV, Z11, UV11, ZFull);

    float4 fogSample = 0;

    [branch]
    if (abs(Z00 - ZFull) < depthThreshold &&
        abs(Z10 - ZFull) < depthThreshold &&
        abs(Z01 - ZFull) < depthThreshold &&
        abs(Z11 - ZFull) < depthThreshold )
    {
        fogSample = SAMPLE_TEXTURE2D_X(_BlurBuffer, sampler_LinearClamp, lowResUV); 
    }
    else
    {
        fogSample = SAMPLE_TEXTURE2D_X(_BlurBuffer, sampler_PointClamp, NearestUV);
    }
    
    return fogSample;
}

float _BlurDepthFalloff;

float4 Blur(float2 uv, float2 delta)
{
    const float4 offset = float4(0, 1, 2, 3);
    const float4 weight = float4(0.266, 0.213, 0.1, 0.036);
    

    float centralDepth = GetHalfDepth(uv);
    float4 result = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv) * weight[0];

    float totalWeight = weight[0];

    [unroll]
    for (int i = 1; i < 4; i++)
    {
        float depth = GetHalfDepth(uv + delta.xy * _BlitTexture_TexelSize.xy * offset[i]);
        float w = abs(centralDepth - depth);
        w = exp(-w * w) * _BlurDepthFalloff;
        result += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + delta.xy * _BlitTexture_TexelSize.xy * offset[i]) * w * weight[i];
        totalWeight += w * weight[i];

        depth = GetHalfDepth(uv - delta.xy * _BlitTexture_TexelSize.xy * offset[i]);
        w = abs(centralDepth - depth);
        w = exp(-w * w) * _BlurDepthFalloff;
        result += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - delta.xy * _BlitTexture_TexelSize.xy * offset[i]) * w * weight[i];
        totalWeight += w * weight[i];
    }


    return result / totalWeight;
}

#endif