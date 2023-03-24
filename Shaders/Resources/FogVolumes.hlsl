#ifndef FOGVOLUMES_INCLUDED
#define FOGVOLUMES_INCLUDED

struct FogVolume
{
    float3 position;
    float radius;
    float density;
    float3 color;
    float fade;
};

StructuredBuffer<FogVolume> _VFogVolumes;

TEXTURE3D(_VFogNoiseTexture);
float _VFogNoiseSize;
float4 _VFogNoiseWeights;
float3 _VFogNoiseSpeed;
float2 _VFogMinMaxNoise;

TEXTURE3D(_VFogCurlTexture);
float _VFogCurlSize;
float _VFogCurlStrength;
float3 _VFogCurlSpeed;

float Curl(float3 pos)
{
    float3 c = SAMPLE_TEXTURE3D_LOD(_VFogCurlTexture, sampler_LinearRepeat, (pos + _Time.y * _VFogCurlSpeed) / _VFogCurlSize, 0);
    c = c * 0.5 - 1;
    return c * _VFogCurlStrength;
}

float GetNoise(float3 pos)
{
    pos += Curl(pos);
    float4 noiseTex = SAMPLE_TEXTURE3D_LOD(_VFogNoiseTexture, sampler_LinearRepeat, (pos + _Time.y * _VFogNoiseSpeed) / _VFogNoiseSize, 0);
    float noise = saturate(dot(noiseTex, _VFogNoiseWeights));
    noise = remap(0, 1, _VFogMinMaxNoise.x, _VFogMinMaxNoise.y, noise);
    return smoothstep(0, 1, noise);
}

float GetDensity(float3 pos, out float3 fogColor)
{
    float density = _VFogDensity;
    float3 col = _VFogColor.rgb;


#if defined(_VFOG_HEIGHTFOG)
        float noise = 1;
        float hmask = (pos.y - _VFogHeightBase) / _VFogHeightTransitionSize;
        float hlerp = saturate(smoothstep(_VFogHeightBase, _VFogHeightBase + _VFogHeightTransitionSize, pos.y));
    
        [branch]
        if (1 - hlerp > 0)
            noise = GetNoise(pos);
    
        density = lerp(_VFogDensity, _VFogHeightFogDensity * noise, 1 - hlerp);
#endif

#if defined(_VFOG_VOLUMES)
    uint validVolumes = 0;
    uint totalVolumes = 0;
    uint stride = 0;
    _VFogVolumes.GetDimensions(totalVolumes, stride);
    float accDensity = 0;
    float3 accColor = 0;
    float accFade = 0;

    [loop]
    for (int i = 0; i < totalVolumes; i++)
    {
        FogVolume v = _VFogVolumes[i];
        float d = distance(v.position, pos);
        
    [branch]
        if (d < v.radius)
        {
            validVolumes++;
            float fade = smoothstep(v.fade, 1, d / v.radius);
            accFade += fade;
            accDensity += v.density;
            accColor += v.color;
        }

    }
    
    [branch]
    if (validVolumes > 0)
    {
        accDensity /= validVolumes;
        accColor /= validVolumes;
        density *= accDensity; //Volumes override base density
        col = accColor;
    }
#endif
    
#if !defined(_VFOG_HEIGHTFOG)
    density *= GetNoise(pos);
#endif
    
    fogColor = col;
    return density * 0.001;
}

float GetDensity(float3 pos)
{
    float3 dummy;
    return GetDensity(pos, dummy);
}

#endif