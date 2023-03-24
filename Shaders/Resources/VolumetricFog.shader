Shader "Hidden/Andicraft/Volumetric Fog"
{
    SubShader
    {
        Tags {"RenderPipeline" = "UniversalPipeline"}
        LOD 100
        ZWrite Off Cull Off

        Pass
        {
            Name "Render Fog"

            HLSLPROGRAM
            
            #include "VolumetricFogUtils.hlsl"
            #include "FogVolumes.hlsl"

            #define TLIMIT 0.001

            #pragma vertex Vert
            #pragma fragment frag
            #pragma target 5.0

            float _Intensity;
            float2 _BlitScale;
            
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            
            #pragma multi_compile_fragment _ _VFOG_HEIGHTFOG
            #pragma multi_compile_fragment _ _VFOG_VOLUMES

            int _DirectionalShadowSteps;
            int _AdditionalShadowSteps;
            int _SelfShadowing;

            float3 CalculateLight(float3 rayPos, float3 rayDir, float totalDistance, InputData inputData, AmbientOcclusionFactor ao)
            {
                // MAIN LIGHT
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(rayPos));
#if defined(_LIGHT_COOKIES)
                real3 cookieColor = SampleMainLightCookie(rayPos);
                mainLight.color *= cookieColor;
#endif
                float cosaMainLight = dot(mainLight.direction, rayDir);
                float3 mainLightCol = mainLight.color * mainLight.shadowAttenuation;

                [branch]
                if (_SelfShadowing && mainLight.shadowAttenuation > 0)
                {
                    float3 lrPos = rayPos;
                    float3 lrDir = -mainLight.direction;
                    float stepSize = _VFogDirShadowDistance / _DirectionalShadowSteps;
                    float transmittance = 1;
                    [loop]
                    for (int i = 0; i < _DirectionalShadowSteps; i++)
                    {
                        float density = GetDensity(lrPos)*_VFogDirShadowDensity;
                        float extinction = max(0.000001, _VFogExtinction + _VFogDirShadowExtinction + density);
                        transmittance *= exp(-extinction * stepSize);

                        if (transmittance <= TLIMIT) break;

                        lrPos += lrDir * stepSize;
                    }
                    mainLightCol *= transmittance;
                }
                mainLightCol *= hg(cosaMainLight);

                inputData.positionWS = rayPos;

                float3 addLightsCol = 0;

                // ADDITIONAL LIGHTS
#if defined(_FORWARD_PLUS) //This kills a warning in the shader compiler but really this thing doesn't really work without forward+
                LIGHT_LOOP_BEGIN(0)
                    Light l = GetAdditionalLight(lightIndex, inputData, 0, ao);
                    float cosa = dot(l.direction, rayDir);
                    addLightsCol += l.color * l.shadowAttenuation * l.distanceAttenuation * hg(cosa);

                    [branch]
                    if (_SelfShadowing && l.shadowAttenuation * l.distanceAttenuation > 0)
                    {
                        float3 lrPos = rayPos;
                        float3 lrDir = -l.direction;
                        float stepSize = _VFogAddShadowDistance / _AdditionalShadowSteps;
                        float transmittance = 1;
                        [loop]
                        for (int i = 0; i < _AdditionalShadowSteps; i++)
                        {
                            float density = GetDensity(lrPos) * _VFogAddShadowDensity * l.distanceAttenuation;
                            float extinction = max(0.000001, _VFogExtinction + _VFogAddShadowExtinction + density);
                            transmittance *= exp(-extinction * stepSize);

                            if (transmittance <= TLIMIT) break;

                            lrPos += lrDir * stepSize;
                        }
                        mainLightCol *= transmittance;
                    }

                LIGHT_LOOP_END
#endif
                // Ambient Lighting
                //ambientSample = CalculateIrradianceFromReflectionProbes(rayDir, rayPos, 1, inp.texcoord) * 1;
                float ambientMip = GetAmbientMip(totalDistance);
                float3 ambientSample = DecodeHDREnvironment(SAMPLE_TEXTURECUBE_LOD(_GlossyEnvironmentCubeMap, sampler_GlossyEnvironmentCubeMap, rayDir, ambientMip), _GlossyEnvironmentCubeMap_HDR);

                return ambientSample + mainLightCol + addLightsCol;
            }

            half4 frag(Varyings inp) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(inp);
                float pixelDepth = GetHalfDepth(inp.texcoord);

                // This function straight up stolen from URPs SSAO
                half3 viewPos = ReconstructViewPos(inp.texcoord, pixelDepth); 
                half3 viewDir = normalize(viewPos);

                // Perspective correction
                float3 centerDir = normalize(ReconstructViewPos(float2(0.5, 0.5), pixelDepth));
                pixelDepth /= dot(viewDir, centerDir);

                float noise = BlueNoise(inp.texcoord);

                // Set up raymarch variables
                float3 startPos = _WorldSpaceCameraPos;
                float3 rayPos = startPos;
                float3 rayDir = viewDir;
                float rayLength = min(pixelDepth, _MaxDistance);

                float stepSize = rayLength / _RaymarchSteps;
                float3 step = rayDir * stepSize;
                
                rayPos += step * noise;

                float totalDistance = stepSize* noise;

                float transmittance = 1;
                float3 totalLight = 0;
                float3 fogColor = 1;

                // We need a dummy InputData and AOFactor to feed to the Additional Lights function
                InputData inputData = (InputData)0;
                inputData.normalizedScreenSpaceUV = inp.texcoord;
                inputData.normalWS = rayDir;
                inputData.viewDirectionWS = viewDir;

                AmbientOcclusionFactor ao = (AmbientOcclusionFactor)0;
                ao.indirectAmbientOcclusion = 1;
                ao.directAmbientOcclusion = 1;

                // Most volumetric fog implementations are using an incorrect transmittance/scattering calculation that causes overdarkening
                // We don't do that here!
                // See https://www.shadertoy.com/view/XlBSRz

                [loop]
                for (uint i = 0; i < _RaymarchSteps; i++)
                {
                    [branch]
                    if (totalDistance > pixelDepth) break; // This _shouldn't_ happen but if it does...
                    
                    float density = GetDensity(rayPos, fogColor);
                    float extinction = max(0.000001, _VFogExtinction + density);

                    // Scattering
                    float3 S = CalculateLight(rayPos, rayDir, totalDistance, inputData, ao) * density * fogColor * _VFogScattering; 
                    float3 Sint = (S - S * exp(-extinction * stepSize)) / extinction;

                    totalLight += Sint * transmittance;
                    transmittance *= exp(-extinction * stepSize);

                    rayPos += rayDir * stepSize;
                    totalDistance += stepSize;

                    if (transmittance <= TLIMIT) break; // No point continuing because we can't see anything past this point anyway
                }

                // Undo the last step
                rayPos -= step;
                totalDistance -= stepSize;

                // Do one final step to the end of the depth buffer, or the sky distance - whichever is lowest
                float skyDistance = min(_VFogSkyDistance, pixelDepth);
                float finalDist = skyDistance - totalDistance;
                rayPos += rayDir * finalDist;
                totalDistance += finalDist;
                if (transmittance > TLIMIT && abs(finalDist) > 0.1)
                {
                    float density = GetDensity(rayPos, fogColor);
                    float extinction = max(0.000001, _VFogExtinction + density);
                    float3 S = CalculateLight(rayPos, rayDir, totalDistance, inputData, ao) * density * fogColor * _VFogScattering;
                    float3 Sint = (S - S * exp(-extinction * finalDist)) / extinction;
                    totalLight += Sint * transmittance;
                    transmittance *= exp(-extinction * finalDist);
                }

                return float4(totalLight, transmittance);

            }
            ENDHLSL
        }

        Pass
        {
            Name "Blur Horizontal"
            HLSLPROGRAM

            #include "VolumetricFogUtils.hlsl"
            #pragma vertex Vert
            #pragma fragment frag
            #pragma target 5.0

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return Blur(input.texcoord, float2(1,0));
            }

            ENDHLSL
        }

        Pass
        {
            Name "Blur Vertical"
            HLSLPROGRAM

            #include "VolumetricFogUtils.hlsl"
            #pragma vertex Vert
            #pragma fragment frag
            #pragma target 5.0
            

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return Blur(input.texcoord, float2(0,1));
            }

            ENDHLSL
        }

        Pass
        {
            Name "Apply Fog"
            HLSLPROGRAM

            #include "VolumetricFogUtils.hlsl"
            #pragma vertex Vert
            #pragma fragment frag
            #pragma target 5.0

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float4 fog = GetNearestColor(input.texcoord);
                float4 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord);
                
                return float4(color.rgb * fog.a + fog.rgb, 1);
            }

            ENDHLSL
        }

        Pass
        {
            Name "Copy Depth"
            HLSLPROGRAM

            #include "VolumetricFogUtils.hlsl"
            #pragma vertex Vert
            #pragma fragment frag
            #pragma target 5.0

            float frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float4 d = _CameraDepthTexture.GatherRed(sampler_CameraDepthTexture, input.texcoord, 0);

                //return min(min(d.x, d.y), min(d.z, d.w));

                return SampleSceneDepth(input.texcoord);
            }

            ENDHLSL
        }

    }
}