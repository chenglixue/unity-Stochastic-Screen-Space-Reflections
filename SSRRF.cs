using System;
using System.Collections.Generic;
using System.Runtime.Remoting.Contexts;
using Unity.Mathematics;
using UnityEditor.iOS;
using UnityEngine.Rendering.RendererUtils;

namespace UnityEngine.Rendering.Universal
{
    public class SSRRF : ScriptableRendererFeature
    {
        #region Variable
        [System.Serializable]
        public class PassSetting
        {
            public string profilerTag = "SSR";
            public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingTransparents;
            
            [Range(1, 10)] public int downSample = 1;
            
            [Range(0, 1f)]   public float thickness     = 0.01f;
            [Range(0, 1000)] public float maxDistance   = 100f;
            [Range(1, 64)]   public int   maxStep       = 16;
            [Range(1, 16)]   public int   binaryCount   = 4;
            [Range(0, 1)]    public float minSmoothness = 0.25f;
            [Range(0, 1)]    public float BRDFBias      = 0.7f;
            [Range(1, 5)]    public float TAAScale      = 1f;
            [Range(0, 0.99f)]public float TAAWeight     = 0.99f;
            [Range(0, 1)]    public float blurIntensity = 1;
            [Range(0, 255)]  public float blurMaxRadius = 32;
            
            public float GetRadius()
            {
                return blurIntensity * blurMaxRadius;
            }

            public LayerMask m_layerMask;

            public enum DebugMode
            {
                Hituv,
                HitDepth,
                HitMask
            }
            public DebugMode debugMode = DebugMode.Hituv;
        }

        public PassSetting m_passSetting = new PassSetting();
        SSRRenderPass m_SSRPass;
        #endregion
        
        public override void Create()
        {
            m_SSRPass = new SSRRenderPass(m_passSetting);
        }
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            m_SSRPass.ConfigureInput(
                ScriptableRenderPassInput.Motion | ScriptableRenderPassInput.Depth
            );
            
            m_SSRPass.Setup((UniversalRenderer)renderer);
            renderer.EnqueuePass(m_SSRPass);
        }
    }
    
    class SSRRenderPass : ScriptableRenderPass
    {
        #region  Variable
        private SSRRF.PassSetting            _passSetting;
        private TAA                          _TAA;
        private Shader                       _shader;
        private ComputeShader                _computeShader;
        private Material                     _material;
        private UniversalRenderer            _Renderer;
        private FilteringSettings            _filteringSettings;
        private SortingCriteria              _sortingCriteria;
        private DrawingSettings              _drawingSettings;
        private SSRRF.PassSetting.DebugMode  _debugMode;
        private const string CommandBufferTag = "Screen Space Reflection Pass";

        private Texture2D _blueNoiseTex;
        private Vector4 _screenSize;
        private Vector4 _rayMarchTexSize;
        private Vector4 _resolvedTexSize;
        private Vector4 _TAATexSize;
        private const int _maxMipMapLevels = 7;
        
        private RenderTextureDescriptor  _descriptor;
        private RenderTargetIdentifier   _cameraRT;
        private RenderTargetIdentifier   _SourceRT;
        private RenderTargetIdentifier[] _GBufferRT = new RenderTargetIdentifier[3];
        private RenderTargetIdentifier[] _HiZDepthRT = new RenderTargetIdentifier[_maxMipMapLevels];
        private RenderTargetIdentifier   _HitUVRT;
        private RenderTargetIdentifier   _HitZRT;
        private RenderTargetIdentifier   _HitMaskRT;
        private RenderTargetIdentifier   _ResolvedRT;
        private RenderTargetIdentifier   _TAART;
        private RenderTargetIdentifier   _TAAPreRT;
        private RenderTargetIdentifier   _CombineRT;
        private static int _SourceTexID       = Shader.PropertyToID("_CameraColorTexture");
        private static int[] _HiZDepthTexID   = new int[_maxMipMapLevels];
        private static int _HitUVTexID        = Shader.PropertyToID("_HitUVTex");
        private static int _HitZTexID         = Shader.PropertyToID("_HitZTex");
        private static int _HitMaskTexID      = Shader.PropertyToID("_HitMaskTex");
        private static int _TAATexID          = Shader.PropertyToID("_TAACurrTex");
        private static int _TAAPreTexID       = Shader.PropertyToID("_TAAPreTex");
        private static int _ResolvedTexID     = Shader.PropertyToID("_ResolvedTex");
        private static int _CombineTexID      = Shader.PropertyToID("_CombineTex");

        private RenderTargetIdentifier[] _HitDataRTIs = new RenderTargetIdentifier[2];
        private RenderTexture[]          _HitDataRTs  = new RenderTexture[2];

        private Matrix4x4 _Pre_Matrix_VP;
        private Matrix4x4 _Curr_Matrix_VP;
        
        #endregion

        #region Setup
        public SSRRenderPass(SSRRF.PassSetting passSetting)
        {
            _passSetting = passSetting;
            this.renderPassEvent = _passSetting.passEvent;
            _TAA = new TAA();
            _debugMode = _passSetting.debugMode;
            
            _shader = Shader.Find("Elysia/SSR");
            _material = CoreUtils.CreateEngineMaterial(_shader);

            _blueNoiseTex = Resources.Load<Texture2D>("Tex/tex_BlueNoise_256x256_UNI");
        }
        
        public void Setup(UniversalRenderer renderer)
        {
            _Renderer = renderer;
        }
        
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            _cameraRT      = _Renderer.cameraColorTarget;
            _GBufferRT[0]  = new RenderTargetIdentifier(Shader.PropertyToID("_GBuffer0"));
            _GBufferRT[1]  = new RenderTargetIdentifier(Shader.PropertyToID("_GBuffer1"));
            _GBufferRT[2]  = new RenderTargetIdentifier(Shader.PropertyToID("_GBuffer2"));
            
            _descriptor                 = renderingData.cameraData.cameraTargetDescriptor;
            _descriptor.msaaSamples     = 1;
            _descriptor.depthBufferBits = 0;
            _screenSize                 = GetTextureSizeParams(new Vector2Int(_descriptor.width, _descriptor.height));
            _rayMarchTexSize            = GetTextureSizeParams(new Vector2Int(_descriptor.width, _descriptor.height ));
            _resolvedTexSize            = GetTextureSizeParams(new Vector2Int(_descriptor.width / _passSetting.downSample, _descriptor.height / _passSetting.downSample));
            _TAATexSize                 = GetTextureSizeParams(new Vector2Int(_descriptor.width, _descriptor.height));

            InitRTI(ref _SourceRT, _SourceTexID, _descriptor, cmd,
                1, 1, RenderTextureFormat.ARGB64, 0, true, true, FilterMode.Point);    
            
            _HiZDepthTexID[0]             = Shader.PropertyToID("_HiZDepthTex0");
            InitRTI(ref _HiZDepthRT[0], _HiZDepthTexID[0], _descriptor, cmd,
                1, 1, RenderTextureFormat.RFloat, 0, true, false, FilterMode.Point);
            
            InitRT(ref _HitDataRTIs[0], ref _HitDataRTs[0], "_SSRHitData", cmd, _material,
                _descriptor, 1, 1, RenderTextureFormat.ARGBFloat, 24, true, true, FilterMode.Point);
            
            InitRT(ref _HitDataRTIs[1], ref _HitDataRTs[1], "_SSRHitMask", cmd, _material,
                _descriptor, 1, 1, RenderTextureFormat.RFloat, 0, true, true, FilterMode.Point);
            
            InitRTI(ref _HitUVRT, _HitUVTexID, _descriptor, cmd,
                1, 1, RenderTextureFormat.RGFloat, 0, true, true, FilterMode.Point);
            
            InitRTI(ref _HitZRT, _HitZTexID, _descriptor, cmd,
                1, 1, RenderTextureFormat.RFloat, 0, true, true, FilterMode.Point);

            InitRTI(ref _HitMaskRT, _HitMaskTexID, _descriptor, cmd,
                1, 1, RenderTextureFormat.RFloat, 0, true, true, FilterMode.Point);
            
            InitRTI(ref _ResolvedRT, _ResolvedTexID, _descriptor, cmd,
                _passSetting.downSample, _passSetting.downSample, RenderTextureFormat.ARGB64, 0, true, true, FilterMode.Point);

            InitRTI(ref _TAART, _TAATexID, _descriptor, cmd,
                1, 1, RenderTextureFormat.ARGB64, 0, true, true, FilterMode.Point);
            
            InitRTI(ref _TAAPreRT, _TAAPreTexID, _descriptor, cmd,
                1, 1, RenderTextureFormat.ARGB64, 0, true, true, FilterMode.Point);
            
            InitRTI(ref _CombineRT, _CombineTexID, _descriptor, cmd,
                1, 1, RenderTextureFormat.DefaultHDR, 0, true, true, FilterMode.Point);
            if (_material != null)
            {
                _material.SetVector("_ViewSize",            _screenSize);
                _material.SetVector("_RayMarchTexSize",     _rayMarchTexSize);
                _material.SetVector("_ResolvedTexSize",     _resolvedTexSize);
                _material.SetVector("_TAATexSize",          _TAATexSize);
                _material.SetVector("_BlueNoiseTexSize",     GetTextureSizeParams(new Vector2Int(_blueNoiseTex.width, _blueNoiseTex.height)));
                _material.SetInt("_MaxStep",         _passSetting.maxStep);
                _material.SetInt("_BinaryCount",     _passSetting.binaryCount);
                _material.SetFloat("_Thickness",     _passSetting.thickness);
                _material.SetFloat("_MaxDistance",   _passSetting.maxDistance);
                _material.SetFloat("_MinSmoothness", _passSetting.minSmoothness);
                _material.SetFloat("_BRDFBias", _passSetting.BRDFBias);
                _material.SetFloat("_TAAScale", _passSetting.TAAScale);
                _material.SetFloat("_TAAWeight", _passSetting.TAAWeight);
                
                cmd.SetGlobalTexture("_BlueNoiseTex", _blueNoiseTex);
                
                var viewMatrix = renderingData.cameraData.GetViewMatrix();
                var projectionMatrix = renderingData.cameraData.GetGPUProjectionMatrix();
                cmd.SetGlobalMatrix("Matrix_V", viewMatrix);
                cmd.SetGlobalMatrix("Matrix_I_V", viewMatrix.inverse);
                cmd.SetGlobalMatrix("Matrix_P", projectionMatrix);
                cmd.SetGlobalMatrix("Matrix_I_P", projectionMatrix.inverse);
                _Curr_Matrix_VP = projectionMatrix * viewMatrix;
                cmd.SetGlobalMatrix("Matrix_VP", _Curr_Matrix_VP);
                cmd.SetGlobalMatrix("Matrix_I_VP", _Curr_Matrix_VP.inverse);
            }
        }

        void InitRTI(ref RenderTargetIdentifier RTI, int texID, RenderTextureDescriptor descriptor, CommandBuffer cmd,
            int downSampleWidth, int downSampleHeight, RenderTextureFormat colorFormat, 
            int depthBufferBits, bool isUseMipmap, bool isAutoGenerateMips,
            FilterMode filterMode)
        {
            descriptor.width           /= downSampleWidth;
            descriptor.height          /= downSampleHeight;
            descriptor.colorFormat      = colorFormat;
            descriptor.depthBufferBits  = depthBufferBits;
            descriptor.useMipMap        = isUseMipmap;
            descriptor.autoGenerateMips = isAutoGenerateMips;
            
            RTI = new RenderTargetIdentifier(texID);
            cmd.GetTemporaryRT(texID, descriptor, filterMode);
            cmd.SetGlobalTexture(texID, RTI);
        }

        void InitRT(ref RenderTargetIdentifier RTI, ref RenderTexture RT, string RTName, CommandBuffer cmd, Material material,
            RenderTextureDescriptor descriptor, int downSampleWidth, int downSampleHeight, RenderTextureFormat colorFormat,
            int depthBufferBits, bool isUseMipmap, bool isAutoGenerateMips, FilterMode filterMode)
        {
            descriptor.width            = descriptor.width / downSampleWidth;
            descriptor.height           = descriptor.height / downSampleHeight;
            descriptor.useMipMap        = isUseMipmap;
            descriptor.autoGenerateMips = isAutoGenerateMips;
            descriptor.depthBufferBits  = depthBufferBits;
            descriptor.colorFormat      = colorFormat;
            RT                          = RenderTexture.GetTemporary(descriptor);
            RT.filterMode               = filterMode;
            RTI                         = new RenderTargetIdentifier(RT);
            material.SetTexture(Shader.PropertyToID(RTName), RT);
        }
        #endregion

        #region Execute
        private Vector4 GetTextureSizeParams(Vector2Int size)
        {
            return new Vector4(size.x, size.y, 1.0f / size.x, 1.0f / size.y);
        }

        void DoCopyDepth(CommandBuffer cmd, Material material)
        {
            try
            {
                if (material == null) return;
                cmd.Blit(null, _HiZDepthTexID[0], material, 0);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        void DoHizDepth(CommandBuffer cmd, ref RenderTargetIdentifier targetRT)
        {
            var computeShader = Resources.Load<ComputeShader>("HiZ/CS_HiZ");
            if (computeShader == null) return;
            
            var tempDesc = _descriptor;
            tempDesc.enableRandomWrite = true;
            tempDesc.colorFormat       = RenderTextureFormat.RFloat;
            tempDesc.useMipMap         = true;
            tempDesc.autoGenerateMips  = false;
            
            Vector2Int currTexSize = new Vector2Int(_descriptor.width, _descriptor.height);
            Vector2Int lastTexSize = currTexSize;
            var lastHizDepthRT = targetRT;
            
            for (int i = 1; i < _maxMipMapLevels; ++i)
            {
                currTexSize.x /= 2;
                currTexSize.y /= 2;

                tempDesc.width = currTexSize.x;
                tempDesc.height = currTexSize.y;
                _HiZDepthTexID[i] = Shader.PropertyToID("_HiZDepthTex" + i);
                _HiZDepthRT[i]    = new RenderTargetIdentifier(_HiZDepthTexID[i]);
                cmd.GetTemporaryRT(_HiZDepthTexID[i], tempDesc, FilterMode.Point);

                int kernelID = computeShader.FindKernel("GetHiZ");
                computeShader.GetKernelThreadGroupSizes(kernelID, out uint x, out uint y, out uint z);
                cmd.SetComputeTextureParam(computeShader, kernelID, Shader.PropertyToID("_SourceTex"), lastHizDepthRT);
                cmd.SetComputeTextureParam(computeShader, kernelID, Shader.PropertyToID("_RW_OutputTex"), _HiZDepthRT[i]);
                cmd.SetComputeVectorParam(computeShader, Shader.PropertyToID("_HiZTexSize"), 
                    new Vector4(1f / lastTexSize.x, 1f / lastTexSize.y, 1f / currTexSize.x, 1f / currTexSize.y));
                cmd.DispatchCompute(computeShader, kernelID,
                    Mathf.CeilToInt((float)currTexSize.x / x),
                    Mathf.CeilToInt((float)currTexSize.y / y),
                    1);
                
                cmd.CopyTexture(_HiZDepthRT[i], 0, 0,targetRT, 0, i);

                lastTexSize = currTexSize;
                lastHizDepthRT = _HiZDepthRT[i];
            }

            for (int i = 1; i < _maxMipMapLevels; ++i)
            {
                cmd.ReleaseTemporaryRT(_HiZDepthTexID[i]);
            }
        }
        
        void DoSSR(CommandBuffer cmd, ref RenderingData renderingData, ScriptableRenderContext context, Material material)
        {
            try
            {
                if (material == null) return;
                
                cmd.SetRenderTarget(_HitDataRTIs, _HitDataRTs[0].depthBuffer);
                
                cmd.BeginSample("SSR Ray March");
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                
                List<ShaderTagId>            _ShaderTagIdList = new List<ShaderTagId>()
                {
                    new ShaderTagId("UniversalGBuffer")
                };
                
                SortingCriteria _sortingCriteria = SortingCriteria.RenderQueue;
                DrawingSettings _drawingSettings = CreateDrawingSettings(_ShaderTagIdList, ref renderingData, _sortingCriteria);
                _drawingSettings.overrideMaterial = _material;
                _drawingSettings.overrideMaterialPassIndex = 1;
                FilteringSettings _filteringSettings = new FilteringSettings(RenderQueueRange.all, _passSetting.m_layerMask);
                
                context.DrawRenderers(renderingData.cullResults, ref _drawingSettings, ref _filteringSettings);
                
                cmd.EndSample("SSR Ray March");
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        void DoDebugHitUV(CommandBuffer cmd, ref RenderTargetIdentifier targetRT, Material material)
        {
            try
            {
                if (material == null) return;
                
                cmd.Blit(null, targetRT, material, 2);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }
        void DoDebugHitDepth(CommandBuffer cmd, ref RenderTargetIdentifier targetRT, Material material)
        {
            try
            {
                if (material == null) return;
                
                cmd.Blit(null, targetRT, material, 3);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }
        void DoDebugHitMask(CommandBuffer cmd, ref RenderTargetIdentifier targetRT, Material material)
        {
            try
            {
                if (material == null) return;
                
                cmd.Blit(null, targetRT, material, 4);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        void DoResolved(CommandBuffer cmd, Material material)
        {
            try
            {
                if (material == null) return;
                
                cmd.Blit(null, _ResolvedRT, material, 5);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        void DoTAA(CommandBuffer cmd, Material material)
        {
            if (material == null) return;
            
            cmd.Blit(_ResolvedRT, _TAART);
            material.SetMatrix("_Pre_Matrix_VP", _Pre_Matrix_VP);
            cmd.Blit(null, _TAART, material, 6);
            cmd.CopyTexture(_TAART, _TAAPreRT);
            
            _Pre_Matrix_VP = _Curr_Matrix_VP;
        }

        void DoCombine(CommandBuffer cmd, Material material)
        {
            try
            {
                if (material == null) return;
                
                cmd.Blit(null, _CombineRT, _material, 7);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }
        
        private void DoKawaseSample(CommandBuffer cmd, RenderTargetIdentifier sourceid, RenderTargetIdentifier targetid,
                                        Vector2Int sourceSize, Vector2Int targetSize,
                                        float offset, bool downSample, ComputeShader computeShader)
        {
            if (!computeShader) return;
            string kernelName = downSample ? "DualBlurDownSample" : "DualBlurUpSample";
            int kernelID = computeShader.FindKernel(kernelName);
            computeShader.GetKernelThreadGroupSizes(kernelID, out uint x, out uint y, out uint z);
            cmd.SetComputeTextureParam(computeShader, kernelID, "_SourceTex", sourceid);
            cmd.SetComputeTextureParam(computeShader, kernelID, "_RW_TargetTex", targetid);
            cmd.SetComputeVectorParam(computeShader, "_SourceSize", GetTextureSizeParams(sourceSize));
            cmd.SetComputeVectorParam(computeShader, "_TargetSize", GetTextureSizeParams(targetSize));
            cmd.SetComputeFloatParam(computeShader, "_BlurOffset", offset);
            cmd.DispatchCompute(computeShader, kernelID,
                                Mathf.CeilToInt((float)targetSize.x / x),
                                Mathf.CeilToInt((float)targetSize.y / y),
                                1);
        }

        private void DoKawaseLinear(CommandBuffer cmd, RenderTargetIdentifier sourceid, RenderTargetIdentifier targetid,
            Vector2Int sourceSize, float offset, ComputeShader computeShader)
        {
            if (!computeShader) return;
            string kernelName = "LerpDownUpTex";
            int kernelID = computeShader.FindKernel(kernelName);
            computeShader.GetKernelThreadGroupSizes(kernelID, out uint x, out uint y, out uint z);
            cmd.SetComputeTextureParam(computeShader, kernelID, "_SourceTex", sourceid);
            cmd.SetComputeTextureParam(computeShader, kernelID, "_RW_TargetTex", targetid);
            cmd.SetComputeVectorParam(computeShader, "_SourceSize", GetTextureSizeParams(sourceSize));
            cmd.SetComputeFloatParam(computeShader, "_BlurOffset", offset);
            cmd.DispatchCompute(computeShader, kernelID,
                                Mathf.CeilToInt((float)sourceSize.x / x),
                                Mathf.CeilToInt((float)sourceSize.y / y),
                                1);
        }
        
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get(CommandBufferTag);

            if (_Renderer == null || _material == null) return;
            
            {
                cmd.Blit(_cameraRT, _SourceRT);
                DoCopyDepth(cmd, _material);
                DoHizDepth(cmd, ref _HiZDepthRT[0]);
                
                DoSSR(cmd, ref renderingData, context, _material);
                switch (_debugMode)
                {
                    case SSRRF.PassSetting.DebugMode.Hituv:
                        DoDebugHitUV(cmd, ref _HitUVRT, _material);
                        break;
                    case SSRRF.PassSetting.DebugMode.HitDepth:
                        DoDebugHitDepth(cmd, ref _HitZRT, _material);
                        break;
                    case SSRRF.PassSetting.DebugMode.HitMask:
                        DoDebugHitMask(cmd, ref _HitMaskRT, _material);
                        break;
                }

                DoResolved(cmd, _material);
                DoTAA(cmd, _material);

                // Blur
                {
                    _computeShader = Resources.Load<ComputeShader>("DualBlur/CS_DualBlur");
                    List<int> rtIDs = new List<int>();
                    List<Vector2Int> rtSizes = new List<Vector2Int>();

                    RenderTextureDescriptor tempDesc = _descriptor;
                    tempDesc.enableRandomWrite = true;
                    string kawaseRT = "_KawaseRT";
                    int kawaseRTID = Shader.PropertyToID(kawaseRT);
                    cmd.GetTemporaryRT(kawaseRTID, tempDesc);
                    
                    rtIDs.Add(kawaseRTID);
                    rtSizes.Add(new Vector2Int((int)_screenSize.x, (int)_screenSize.y));

                    float downSampleAmount = Mathf.Log(_passSetting.GetRadius() + 1.0f) / 0.693147181f;
                    int downSampleCount = Mathf.FloorToInt(downSampleAmount);
                    float offsetRatio = downSampleAmount - (float)downSampleCount;

                    Vector2Int lastSize = new Vector2Int((int)_screenSize.x, (int)_screenSize.y);
                    int lastID = _TAATexID;
                    for (int i = 0; i <= downSampleCount; i++)
                    {
                        string rtName = "_KawaseRT" + i.ToString();
                        int rtID = Shader.PropertyToID(rtName);
                        Vector2Int rtSize = new Vector2Int((lastSize.x + 1) / 2, (lastSize.y + 1) / 2);
                        tempDesc.width = rtSize.x;
                        tempDesc.height = rtSize.y;
                        cmd.GetTemporaryRT(rtID, tempDesc);

                        rtIDs.Add(rtID);
                        rtSizes.Add(rtSize);

                        DoKawaseSample(cmd, lastID, rtID, lastSize, rtSize, 1.0f, true, _computeShader);
                        lastSize = rtSize;
                        lastID = rtID;
                    }

                    if(downSampleCount == 0)
                    {
                        DoKawaseSample(cmd, rtIDs[1], rtIDs[0], rtSizes[1], rtSizes[0], 1.0f, false, _computeShader);
                        DoKawaseLinear(cmd, _cameraRT, rtIDs[0], rtSizes[0], offsetRatio, _computeShader);
                    }
                    else
                    {
                        string intermediateRTName = "_KawaseRT" + (downSampleCount + 1).ToString();
                        int intermediateRTID = Shader.PropertyToID(intermediateRTName);
                        Vector2Int intermediateRTSize = rtSizes[downSampleCount];
                        tempDesc.width = intermediateRTSize.x;
                        tempDesc.height = intermediateRTSize.y;
                        cmd.GetTemporaryRT(intermediateRTID, tempDesc);
                        
                        for (int i = downSampleCount+1; i >= 1; i--)
                        {
                            int sourceID = rtIDs[i];
                            Vector2Int sourceSize = rtSizes[i];
                            int targetID = i == (downSampleCount + 1) ? intermediateRTID : rtIDs[i - 1];
                            Vector2Int targetSize = rtSizes[i - 1];
                        
                            DoKawaseSample(cmd, sourceID, targetID, sourceSize, targetSize, 1.0f, false, _computeShader);
                        
                            if (i == (downSampleCount + 1))
                            {
                                DoKawaseLinear(cmd, rtIDs[i - 1], intermediateRTID, targetSize, offsetRatio, _computeShader);
                                int tempID = intermediateRTID;
                                intermediateRTID = rtIDs[i - 1];
                                rtIDs[i - 1] = tempID;
                            }
                            cmd.ReleaseTemporaryRT(sourceID);
                        }
                        cmd.ReleaseTemporaryRT(intermediateRTID);
                    }

                    if (_passSetting.blurIntensity != 0 && _passSetting.blurMaxRadius != 0)
                    {
                        cmd.Blit(kawaseRTID, _TAART);
                        cmd.ReleaseTemporaryRT(kawaseRTID);
                    }   
                }
                
                DoCombine(cmd, _material);
                cmd.Blit(_CombineRT, _cameraRT);
            }
            
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
        
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(_SourceTexID);
            cmd.ReleaseTemporaryRT(_HitUVTexID);
            cmd.ReleaseTemporaryRT(_HitZTexID);
            cmd.ReleaseTemporaryRT(_HitMaskTexID);
            cmd.ReleaseTemporaryRT(_ResolvedTexID);
            cmd.ReleaseTemporaryRT(_TAATexID);
            cmd.ReleaseTemporaryRT(_TAAPreTexID);
            cmd.ReleaseTemporaryRT(_CombineTexID);
            cmd.ReleaseTemporaryRT(Shader.PropertyToID("_GBuffer0"));
            cmd.ReleaseTemporaryRT(Shader.PropertyToID("_GBuffer1"));
            cmd.ReleaseTemporaryRT(Shader.PropertyToID("_GBuffer2"));
            RenderTexture.ReleaseTemporary(_HitDataRTs[0]);
            RenderTexture.ReleaseTemporary(_HitDataRTs[1]);
            for (int i = 0; i < _maxMipMapLevels; ++i)
            {
                cmd.ReleaseTemporaryRT(_HiZDepthTexID[i]);
            }
        }
        #endregion
    }

    class TAA
    {
        private int m_SampleIndex = 0;
        private const int k_SampleCount = 64;
        
        private float GetHaltonValue(int index, int radix)
        {
            float result = 0f;
            float fraction = 1f / (float) radix;

            while (index > 0)
            {
                result += (float) (index % radix) * fraction;

                index /= radix;
                fraction /= (float) radix;
            }

            return result;
        }
        
        public Vector2 GenerateRandomOffset()
        {
            var offset = new Vector2(
                GetHaltonValue(m_SampleIndex & 1023, 2),
                GetHaltonValue(m_SampleIndex & 1023, 3));

            if (++m_SampleIndex >= k_SampleCount)
                m_SampleIndex = 0;

            return offset;
        }
    }
}