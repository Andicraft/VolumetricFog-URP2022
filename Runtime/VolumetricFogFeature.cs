using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Andicraft.VolumetricFog
{
    public class VolumetricFogFeature : ScriptableRendererFeature
    {
        private VolumetricFogRenderPass renderPass;
        private ShaderParameters parameters;

        [Range(8, 128)]
        public int raymarchSteps = 16;
        [Min(50)]
        public float maxDistance = 250;
        [Min(25)]
        public float lowestMipDistance = 100;
        [Min(0)]
        public float lowestMip = 1;
        [Min(0.001f)]
        public float blurDepthFalloff = 1.0f;
        [Space(16)]
        [Tooltip("VERY expensive. You should probably not use this.")]
        public bool selfShadowing = false;
        [Range(1, 8)]
        public int directionalShadowSteps = 2;
        [Range(1, 8)]
        public int additionalLightShadowSteps = 2;

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (renderingData.cameraData.isPreviewCamera == false || renderingData.cameraData.cameraType == CameraType.Reflection)
            {
                parameters.maxDistance = maxDistance;
                parameters.raymarchSteps = raymarchSteps;
                parameters.selfShadowing = selfShadowing;
                parameters.directionalShadowSteps = directionalShadowSteps;
                parameters.additionalShadowSteps = additionalLightShadowSteps;
                parameters.lowestMip = lowestMip;
                parameters.lowestMipDistance = lowestMipDistance;
                parameters.blurDepthFalloff = blurDepthFalloff;

                renderPass.Setup(renderingData, parameters);
                renderer.EnqueuePass(renderPass);
            }
        }

        public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
        {
            if (renderingData.cameraData.isPreviewCamera == false || renderingData.cameraData.cameraType == CameraType.Reflection)
            {
                renderPass.ConfigureInput(ScriptableRenderPassInput.Color & ScriptableRenderPassInput.Depth);
            }
        }

        public override void Create()
        {
            renderPass ??= new();
            parameters = new ShaderParameters();
        }

        protected override void Dispose(bool disposing)
        {
            renderPass?.Dispose();
            renderPass = null;
        }

        internal struct ShaderParameters
        {
            public float maxDistance;
            public int raymarchSteps;
            public bool selfShadowing;
            public int directionalShadowSteps;
            public int additionalShadowSteps;
            public float lowestMipDistance;
            public float lowestMip;
            public float blurDepthFalloff;
        }

        /// <summary>
        /// Handles the actual rendering of the Volumetric Fog
        /// </summary>
        class VolumetricFogRenderPass : ScriptableRenderPass
        {
            RTHandle copiedColor;
            RTHandle fogBuffer;
            RTHandle blurBuffer, blurBuffer2;
            RTHandle halfDepthBuffer;
            Material renderMaterial;
            ShaderParameters parameters;
            GlobalKeyword kw_VolumesEnabled;

            int renderFogPass;
            int blurPassH, blurPassV;
            int applyFogPass;
            int copyDepthPass;
            int upscaleFogPass;

            private Vector4[] m_CameraTopLeftCorner = new Vector4[2];
            private Vector4[] m_CameraXExtent = new Vector4[2];
            private Vector4[] m_CameraYExtent = new Vector4[2];
            private Vector4[] m_CameraZExtent = new Vector4[2];
            private Matrix4x4[] m_CameraViewProjections = new Matrix4x4[2];

            private ComputeBuffer m_FogVolumesBuffer;

            public VolumetricFogRenderPass()
            {
                base.profilingSampler = new ProfilingSampler("Volumetric Fog Pass");
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                Shader shader = Shader.Find("Hidden/Andicraft/Volumetric Fog");
                renderMaterial = CoreUtils.CreateEngineMaterial(shader);
            }

            public void Setup(RenderingData renderingData, ShaderParameters p)
            {
                parameters = p;

                kw_VolumesEnabled = GlobalKeyword.Create("_VFOG_VOLUMES");

                renderFogPass = renderMaterial.FindPass("Render Fog");
                blurPassH = renderMaterial.FindPass("Blur Horizontal");
                blurPassV = renderMaterial.FindPass("Blur Vertical");
                applyFogPass = renderMaterial.FindPass("Apply Fog");
                copyDepthPass = renderMaterial.FindPass("Copy Depth");
                upscaleFogPass = renderMaterial.FindPass("Upscale Fog");

                renderMaterial.SetVector("_BlitScaleBias", new Vector4(1, 1, 0, 0));
                renderMaterial.SetFloat(s_maxDistance, p.maxDistance);
                renderMaterial.SetInteger(s_raymarchSteps, p.raymarchSteps);
                renderMaterial.SetInteger(s_selfShadowing, p.selfShadowing ? 1 : 0);
                renderMaterial.SetFloat(s_lowestMip, parameters.lowestMip);
                renderMaterial.SetFloat(s_lowestMipDistance, parameters.lowestMipDistance);
                renderMaterial.SetFloat(s_blurDepthFalloff, parameters.blurDepthFalloff);
                renderMaterial.SetInteger(s_directionalShadowSteps, parameters.directionalShadowSteps);
                renderMaterial.SetInteger(s_additionalShadowSteps, parameters.additionalShadowSteps);

                var colorCopyDescriptor = renderingData.cameraData.cameraTargetDescriptor;
                colorCopyDescriptor.depthBufferBits = 0;
                RenderingUtils.ReAllocateIfNeeded(ref copiedColor, colorCopyDescriptor, name: "_VolumetricFogColorCopy");

                RenderTextureDescriptor fogBufferDescriptor = colorCopyDescriptor;
                fogBufferDescriptor.colorFormat = RenderTextureFormat.DefaultHDR;

                fogBufferDescriptor.width = colorCopyDescriptor.width / 2;
                fogBufferDescriptor.height = colorCopyDescriptor.height / 2;


                RenderingUtils.ReAllocateIfNeeded(ref fogBuffer, fogBufferDescriptor, name: "_VolumetricFogBuffer0");
                RenderingUtils.ReAllocateIfNeeded(ref blurBuffer, fogBufferDescriptor, name: "_VolumetricFogBuffer1");
                RenderingUtils.ReAllocateIfNeeded(ref blurBuffer2, fogBufferDescriptor, name: "_VolumetricFogBuffer2");

                RenderTextureDescriptor halfDepthDescriptor = fogBufferDescriptor;
                halfDepthDescriptor.colorFormat = RenderTextureFormat.RFloat;
                RenderingUtils.ReAllocateIfNeeded(ref halfDepthBuffer, halfDepthDescriptor, name: "_VolumetricFogBuffer3");
            }

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
#if ENABLE_VR && ENABLE_XR_MODULE
                int eyeCount = renderingData.cameraData.xr.enabled && renderingData.cameraData.xr.singlePassEnabled ? 2 : 1;
#else
                int eyeCount = 1;
#endif
                for (int eyeIndex = 0; eyeIndex < eyeCount; eyeIndex++)
                {
                    Matrix4x4 view = renderingData.cameraData.GetViewMatrix(eyeIndex);
                    Matrix4x4 proj = renderingData.cameraData.GetProjectionMatrix(eyeIndex);
                    m_CameraViewProjections[eyeIndex] = proj * view;

                    // camera view space without translation, used by SSAO.hlsl ReconstructViewPos() to calculate view vector.
                    Matrix4x4 cview = view;
                    cview.SetColumn(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
                    Matrix4x4 cviewProj = proj * cview;
                    Matrix4x4 cviewProjInv = cviewProj.inverse;

                    Vector4 topLeftCorner = cviewProjInv.MultiplyPoint(new Vector4(-1, 1, -1, 1));
                    Vector4 topRightCorner = cviewProjInv.MultiplyPoint(new Vector4(1, 1, -1, 1));
                    Vector4 bottomLeftCorner = cviewProjInv.MultiplyPoint(new Vector4(-1, -1, -1, 1));
                    Vector4 farCentre = cviewProjInv.MultiplyPoint(new Vector4(0, 0, 1, 1));
                    m_CameraTopLeftCorner[eyeIndex] = topLeftCorner;
                    m_CameraXExtent[eyeIndex] = topRightCorner - topLeftCorner;
                    m_CameraYExtent[eyeIndex] = bottomLeftCorner - topLeftCorner;
                    //m_CameraZExtent[eyeIndex] = farCentre;
                }

                renderMaterial.SetVector(s_ProjectionParams2ID, new Vector4(1.0f / renderingData.cameraData.camera.nearClipPlane, 0.0f, 0.0f, 0.0f));
                renderMaterial.SetMatrixArray(s_CameraViewProjectionsID, m_CameraViewProjections);
                renderMaterial.SetVectorArray(s_CameraViewTopLeftCornerID, m_CameraTopLeftCorner);
                renderMaterial.SetVectorArray(s_CameraViewXExtentID, m_CameraXExtent);
                renderMaterial.SetVectorArray(s_CameraViewYExtentID, m_CameraYExtent);
                //renderMaterial.SetVectorArray(s_CameraViewZExtentID, m_CameraZExtent);

                VolumetricFog vfog;
                if (Application.isPlaying) vfog = VolumetricFog.instance;
                else vfog = VolumetricFog.FindObjectOfType<VolumetricFog>();

                if (vfog && vfog.volumes.Count > 0)
                {
                    if (m_FogVolumesBuffer != null) m_FogVolumesBuffer.Dispose();
                    m_FogVolumesBuffer = new ComputeBuffer(vfog.volumes.Count, FogDensityVolume.BufferStride, ComputeBufferType.Structured, ComputeBufferMode.Immutable);
                    FogDensityVolume.FogVolumeBufferStruct[] structs = new FogDensityVolume.FogVolumeBufferStruct[vfog.volumes.Count];
                    for (int i = 0; i < vfog.volumes.Count; i++)
                    {
                        structs[i] = vfog.volumes[i].bufferStruct;
                    }
                    m_FogVolumesBuffer.SetData(structs);
                    renderMaterial.SetBuffer("_VFogVolumes", m_FogVolumesBuffer);
                }

                Shader.SetKeyword(kw_VolumesEnabled, vfog && vfog.volumes.Count != 0);
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (renderingData.cameraData.isPreviewCamera || renderingData.cameraData.cameraType == CameraType.Reflection)
                    return;

                VolumetricFog vfog;
                if (Application.isPlaying) vfog = VolumetricFog.instance;
                else vfog = VolumetricFog.FindObjectOfType<VolumetricFog>();

                if (!vfog) return;
                if (!vfog.enabled) return;

                var cameraData = renderingData.cameraData;
                var source = cameraData.renderer.cameraColorTargetHandle;

                CommandBuffer cmd = CommandBufferPool.Get("Volumetric Fog");

                // Copy color to texture
                Blitter.BlitCameraTexture(cmd, source, copiedColor);

                // Create half-size depth target
                CoreUtils.SetRenderTarget(cmd, halfDepthBuffer);
                CoreUtils.DrawFullScreen(cmd, renderMaterial, shaderPassId: copyDepthPass);
                renderMaterial.SetTexture("_HalfDepthBuffer", halfDepthBuffer);

                // Render Fog
                CoreUtils.SetRenderTarget(cmd, fogBuffer);
                CoreUtils.DrawFullScreen(cmd, renderMaterial, shaderPassId: renderFogPass);

                // Blur Fog
                //renderMaterial.SetTexture("_VFogBuffer", fogBuffer);
                Blitter.BlitCameraTexture(cmd, fogBuffer, blurBuffer, renderMaterial, blurPassH);
                Blitter.BlitCameraTexture(cmd, blurBuffer, blurBuffer2, renderMaterial, blurPassV);

                Blitter.BlitCameraTexture(cmd, blurBuffer2, blurBuffer, renderMaterial, blurPassH);
                Blitter.BlitCameraTexture(cmd, blurBuffer, blurBuffer2, renderMaterial, blurPassV);

                renderMaterial.SetTexture("_BlurBuffer", blurBuffer2);

                // Apply Fog
                renderMaterial.SetTexture("_BlitTexture", copiedColor);
                CoreUtils.SetRenderTarget(cmd, cameraData.renderer.cameraColorTargetHandle);
                CoreUtils.DrawFullScreen(cmd, renderMaterial, shaderPassId: applyFogPass);

                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

            }

            public void Dispose()
            {
                copiedColor?.Release();
                fogBuffer?.Release();
                blurBuffer?.Release();
                blurBuffer2?.Release();
                m_FogVolumesBuffer?.Dispose();
            }
        }

        private static readonly int s_CameraViewXExtentID = Shader.PropertyToID("_CameraViewXExtent");
        private static readonly int s_CameraViewYExtentID = Shader.PropertyToID("_CameraViewYExtent");
        private static readonly int s_CameraViewZExtentID = Shader.PropertyToID("_CameraViewZExtent");
        private static readonly int s_ProjectionParams2ID = Shader.PropertyToID("_ProjectionParams2");
        private static readonly int s_CameraViewProjectionsID = Shader.PropertyToID("_CameraViewProjections");
        private static readonly int s_CameraViewTopLeftCornerID = Shader.PropertyToID("_CameraViewTopLeftCorner");
        private static readonly int s_raymarchSteps = Shader.PropertyToID("_RaymarchSteps");
        private static readonly int s_maxDistance = Shader.PropertyToID("_MaxDistance");
        private static readonly int s_selfShadowing = Shader.PropertyToID("_SelfShadowing");
        private static readonly int s_directionalShadowSteps = Shader.PropertyToID("_DirectionalShadowSteps");
        private static readonly int s_additionalShadowSteps = Shader.PropertyToID("_AdditionalShadowSteps");
        private static readonly int s_lowestMipDistance = Shader.PropertyToID("_LowestMipDistance");
        private static readonly int s_lowestMip = Shader.PropertyToID("_LowestMip");
        private static readonly int s_blurDepthFalloff = Shader.PropertyToID("_BlurDepthFalloff");
    }
}
