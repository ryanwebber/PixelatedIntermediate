using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class ScreenSpacePixelation : ScriptableRendererFeature
{
    private static class ShaderUtils {
        public static List<ShaderTagId> DefaultShaderTags = new List<ShaderTagId>{
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly"),
            new ShaderTagId("LightweightForward"),
            new ShaderTagId("SRPDefaultUnlit"),
        };
    }

    private sealed class RenderDepthTexturePass : ScriptableRenderPass
    {
        private RTHandle targetHandle;
        private RTHandle temporaryDepthBuffer;
        private LayerMask layerMask;
        private Material material;
        private Vector2Int resolution;

        private int targetHandlePropertyIdentifier => Shader.PropertyToID(targetHandle.name);
        private int temporaryDepthBufferPropertyIdentifier => Shader.PropertyToID(temporaryDepthBuffer.name);

        public RenderDepthTexturePass(RTHandle targetHandle, LayerMask layerMask, Material material, Vector2Int resolution)
        {
            this.targetHandle = targetHandle;
            this.layerMask = layerMask;
            this.material = material;
            this.resolution = resolution;
            this.temporaryDepthBuffer = RTHandles.Alloc(
                scaleFactor: Vector2.one,
                depthBufferBits: DepthBits.Depth32,
                dimension: TextureDimension.Tex2D,
                name:"_ScreenSpaceSceneTexture");
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var textureDescriptor = targetHandle.rt.descriptor;
            textureDescriptor.width = resolution.x;
            textureDescriptor.height = resolution.y;

            cmd.GetTemporaryRT(targetHandlePropertyIdentifier, textureDescriptor, FilterMode.Point);

            var depthBufferDescriptor = temporaryDepthBuffer.rt.descriptor;
            depthBufferDescriptor.stencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None;
            depthBufferDescriptor.width = resolution.x;
            depthBufferDescriptor.height = resolution.y;

            cmd.GetTemporaryRT(temporaryDepthBufferPropertyIdentifier, depthBufferDescriptor, FilterMode.Point);

            ConfigureTarget(temporaryDepthBuffer);
            ConfigureClear(ClearFlag.Depth | ClearFlag.Color, Color.clear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (!material)
                return;

            var cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, new ProfilingSampler("ScriptableRenderPass_RenderDepthBuffer")))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                // 1. Render and store the depth buffer

                var drawSettings = CreateDrawingSettings(ShaderUtils.DefaultShaderTags, ref renderingData, SortingCriteria.CommonOpaque);
                var filteringSettings = new FilteringSettings(null);
                filteringSettings.layerMask = layerMask;

                context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filteringSettings);

                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                // 2. Blit over the depth buffer as a color attachment
                material.SetFloat("_Far_Clipping_Distance", renderingData.cameraData.camera.farClipPlane);
                Blit(cmd, temporaryDepthBuffer, targetHandle, material, passIndex: 0);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(targetHandlePropertyIdentifier);
            cmd.ReleaseTemporaryRT(temporaryDepthBufferPropertyIdentifier);
        }
    }

    private sealed class RenderObjectsPass : ScriptableRenderPass
    {
        private RTHandle targetHandle;
        private LayerMask layerMask;
        private Material material;
        private Vector2Int resolution;

        private int targetHandlePropertyIdentifier => Shader.PropertyToID(targetHandle.name);

        public RenderObjectsPass(RTHandle targetHandle, LayerMask layerMask, Material material, Vector2Int resolution)
        {
            this.targetHandle = targetHandle;
            this.layerMask = layerMask;
            this.material = material;
            this.resolution = resolution;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var textureDescriptor = targetHandle.rt.descriptor;
            textureDescriptor.width = resolution.x;
            textureDescriptor.height = resolution.y;

            cmd.GetTemporaryRT(targetHandlePropertyIdentifier, textureDescriptor, FilterMode.Point);
            ConfigureTarget(targetHandle);
            ConfigureClear(ClearFlag.Color | ClearFlag.Depth, Color.clear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, new ProfilingSampler("ScriptableRenderPass_RenderObjects")))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                var drawSettings = CreateDrawingSettings(ShaderUtils.DefaultShaderTags, ref renderingData, SortingCriteria.CommonOpaque);

                if (material != null)
                    drawSettings.overrideMaterial = material;

                var filteringSettings = new FilteringSettings(RenderQueueRange.opaque);
                filteringSettings.layerMask = layerMask;

                context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filteringSettings);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(targetHandlePropertyIdentifier);
        }
    }

    private sealed class RenderPixelatedScenePass : ScriptableRenderPass
    {
        private Material material;
        private LayerMask layerMask;
        private RTHandle targetHandle;
        private RTHandle pixelatedDepthTexture;
        private RTHandle pixelatedColorTexture;
        private RTHandle temporaryColorTexture;

        private int targetHandlePropertyIdentifier => Shader.PropertyToID(targetHandle.name);
        private int temporaryTargetHandlePropertyIdentifier => Shader.PropertyToID(temporaryColorTexture.name);

        public RenderPixelatedScenePass(RTHandle targetHandle, RTHandle pixelatedDepthTexture, RTHandle pixelatedColorTexture, LayerMask layerMask, Material material)
        {
            this.material = material;
            this.layerMask = layerMask;
            this.targetHandle = targetHandle;
            this.pixelatedDepthTexture = pixelatedDepthTexture;
            this.pixelatedColorTexture = pixelatedColorTexture;
            this.temporaryColorTexture = RTHandles.Alloc(
                scaleFactor: Vector2.one,
                dimension: TextureDimension.Tex2D,
                name:"_TemporarySceneColorTexture");
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var textureDescriptor = temporaryColorTexture.rt.descriptor;
            textureDescriptor.width = renderingData.cameraData.cameraTargetDescriptor.width;
            textureDescriptor.height = renderingData.cameraData.cameraTargetDescriptor.height;

            RenderingUtils.ReAllocateIfNeeded(ref temporaryColorTexture, textureDescriptor);

            cmd.GetTemporaryRT(targetHandlePropertyIdentifier, renderingData.cameraData.cameraTargetDescriptor, FilterMode.Point);
            cmd.GetTemporaryRT(temporaryTargetHandlePropertyIdentifier, textureDescriptor, FilterMode.Point);

            ConfigureTarget(temporaryColorTexture, renderingData.cameraData.renderer.cameraDepthTargetHandle);
            ConfigureClear(ClearFlag.Color | ClearFlag.Depth, Color.red);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (!material)
                return;

            var cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, new ProfilingSampler("ScriptableRenderPass_RenderScene")))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                // 1. Draw the non-pixelated object scene, and let's have it write a depth buffer

                var drawSettings = CreateDrawingSettings(ShaderUtils.DefaultShaderTags, ref renderingData, SortingCriteria.CommonOpaque);
                var filteringSettings = new FilteringSettings(null);
                filteringSettings.layerMask = layerMask;

                context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filteringSettings);

                // 2. Blend the non-pixelated scene texture and pixelated scene texture

                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                material.SetTexture("_PixelationDepthTexture", pixelatedDepthTexture.rt);
                material.SetTexture("_PixelationColorTexture", pixelatedColorTexture.rt);

                Blit(cmd, temporaryColorTexture, targetHandle, material, passIndex: 0);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(targetHandlePropertyIdentifier);
            cmd.ReleaseTemporaryRT(temporaryTargetHandlePropertyIdentifier);
        }
    }

    private sealed class BlitToScreenPass: ScriptableRenderPass
    {
        private RTHandle texture;
        private Material material;

        public BlitToScreenPass(RTHandle texture, Material material)
        {
            this.texture = texture;
            this.material = material;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, new ProfilingSampler("RenderObjects")))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                Blit(cmd, texture, renderingData.cameraData.renderer.cameraColorTargetHandle, material);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    [SerializeField]
    private RenderPassEvent renderPassEvent;

    [SerializeField]
    private LayerMask pixelationLayers;

    [SerializeField]
    private LayerMask unpixelatedLayers;

    [SerializeField]
    [Tooltip("The material that can render depth information for the pixelated layers into a texture")]
    private Material depthRenderingMaterial;

    [SerializeField]
    [Tooltip("The material that can blend the pixelation color texture and depth texture into the rendered scene")]
    private Material sceneBlendMaterial;

    [SerializeField]
    [Tooltip("The material is responsible for copying the final scene texture to the screen")]
    private Material blitMaterial;

    [SerializeField]
    [Tooltip("The pixelation resolution to use for the pixelation downscaling")]
    private Vector2Int pixelationResolution = new Vector2Int(480, 270);

    private RenderDepthTexturePass renderPixelationDepthTexturePass;
    private RenderObjectsPass renderPixelationObjectsPass;
    private RenderPixelatedScenePass renderPixelatedScenePass;
    private BlitToScreenPass blitToScreenPass;
    // private List<RTHandle> handles;

    public override void Create()
    {
        var temporaryDepthBufferHandle = RTHandles.Alloc(
            scaleFactor: Vector2.one,
            dimension: TextureDimension.Tex2D,
            name: "_ScreenSpacePixelationDepthTexture");

        var temporaryColorBufferHandle = RTHandles.Alloc(
            scaleFactor: Vector2.one,
            dimension: TextureDimension.Tex2D,
            name:"_ScreenSpacePixelationColorTexture");

        var temporarySceneTextureHandle = RTHandles.Alloc(
            scaleFactor: Vector2.one,
            dimension: TextureDimension.Tex2D,
            name:"_ScreenSpaceSceneTexture");

        this.renderPixelationDepthTexturePass = new (temporaryDepthBufferHandle, pixelationLayers, depthRenderingMaterial, pixelationResolution)
        {
            renderPassEvent = this.renderPassEvent
        };

        this.renderPixelationObjectsPass = new (temporaryColorBufferHandle, pixelationLayers, null, pixelationResolution)
        {
            renderPassEvent = this.renderPassEvent
        };
        
        this.renderPixelatedScenePass = new (temporarySceneTextureHandle, temporaryDepthBufferHandle, temporaryColorBufferHandle, unpixelatedLayers, sceneBlendMaterial)
        {
            renderPassEvent = this.renderPassEvent
        };

        this.blitToScreenPass = new (temporarySceneTextureHandle, null)
        {
            renderPassEvent = this.renderPassEvent
        };

        // this.handles = new()
        // {
        //     temporaryDepthBufferHandle,
        //     temporaryColorBufferHandle,
        //     temporarySceneTextureHandle,
        // };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(renderPixelationDepthTexturePass);
        renderer.EnqueuePass(renderPixelationObjectsPass);
        renderer.EnqueuePass(renderPixelatedScenePass);
        renderer.EnqueuePass(blitToScreenPass);
    }

    // private void OnDestroy()
    // {
    //     foreach (var handle in handles)
    //         handle.Release();
    //     handles = null;   
    // }
}
