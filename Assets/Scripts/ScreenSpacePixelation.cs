using System;
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

    private class SharedHandle
    {            
        public RTHandle handle;

        public int Identifier => Shader.PropertyToID(handle.name);

        public SharedHandle(System.Func<RTHandle> initializer)
        {
            this.handle = initializer();
        }

        public void Configure(RenderTextureDescriptor descriptor) => RenderingUtils.ReAllocateIfNeeded(ref handle, descriptor);
    }

    private class RenderPassSettings
    {
        public delegate void UpdateMaterial(Material mat, ref RenderingData renderingData);

        public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        public LayerMask layerMask = ~0;
        public DepthBits depthBits = DepthBits.None;
        public Material overrideMaterial = null;
        public Material blitMaterial = null;
        public Nullable<Vector2Int> overrideResolution = null;

        public UpdateMaterial onPreRender = null;
        public UpdateMaterial onPreBlit = null;
    }

    private sealed class RenderObjectsPass : ScriptableRenderPass
    {
        private RenderPassSettings settings;
        private SharedHandle targetHandle;
        private SharedHandle temporaryColorBuffer;

        public RenderObjectsPass(SharedHandle targetHandle, RenderPassSettings settings)
        {
            this.settings = settings;
            this.targetHandle = targetHandle;
            this.temporaryColorBuffer = new(() => 
                RTHandles.Alloc(
                    scaleFactor: Vector2.one,
                    depthBufferBits: settings.depthBits,
                    dimension: TextureDimension.Tex2D,
                    name: "_TemporaryScreenSpaceRenderTexture")
            );
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var textureDescriptor = targetHandle.handle.rt.descriptor;
            if (settings.overrideResolution != null)
            {
                textureDescriptor.width = settings.overrideResolution.Value.x;
                textureDescriptor.height = settings.overrideResolution.Value.y;
            }
            else
            {
                textureDescriptor.width = renderingData.cameraData.cameraTargetDescriptor.width;
                textureDescriptor.height = renderingData.cameraData.cameraTargetDescriptor.height;
            }

            targetHandle.Configure(textureDescriptor);
            cmd.GetTemporaryRT(targetHandle.Identifier, textureDescriptor, FilterMode.Point);

            var temporaryDescriptor = temporaryColorBuffer.handle.rt.descriptor;
            temporaryDescriptor.width = renderingData.cameraData.cameraTargetDescriptor.width;
            temporaryDescriptor.height = renderingData.cameraData.cameraTargetDescriptor.height;

            if (temporaryDescriptor.depthBufferBits > 0)
            {
                temporaryDescriptor.colorFormat = RenderTextureFormat.Depth;
                temporaryDescriptor.stencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None;
            }

            temporaryColorBuffer.Configure(temporaryDescriptor);
            cmd.GetTemporaryRT(temporaryColorBuffer.Identifier, temporaryDescriptor, FilterMode.Point);

            if (temporaryDescriptor.depthBufferBits > 0)
                ConfigureTarget(temporaryColorBuffer.handle);
            else
                ConfigureTarget(temporaryColorBuffer.handle, renderingData.cameraData.renderer.cameraDepthTargetHandle);

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

                if (settings.overrideMaterial != null)
                {
                    drawSettings.overrideMaterial = settings.overrideMaterial;
                    settings.onPreRender?.Invoke(settings.overrideMaterial, ref renderingData);
                }

                var filteringSettings = new FilteringSettings(RenderQueueRange.opaque);
                filteringSettings.layerMask = settings.layerMask;

                context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filteringSettings);

                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                if (settings.blitMaterial != null)
                    settings.onPreBlit?.Invoke(settings.blitMaterial, ref renderingData);

                Blit(cmd, temporaryColorBuffer.handle, targetHandle.handle, settings.blitMaterial, passIndex: 0);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(targetHandle.Identifier);
            cmd.ReleaseTemporaryRT(temporaryColorBuffer.Identifier);
        }
    }

    private sealed class BlitToScreenPass: ScriptableRenderPass
    {
        private SharedHandle texture;
        private Material material;

        public BlitToScreenPass(SharedHandle texture, Material material)
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

                Blit(cmd, texture.handle, renderingData.cameraData.renderer.cameraColorTargetHandle, material);
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
    [Tooltip("The material that can render depth information for the pixelated layers into a texture")]
    private Material depthRenderingMaterial;

    [SerializeField]
    [Tooltip("The material that can render normal information for the pixelated layers into a texture")]
    private Material normalsRenderingMaterial;

    [SerializeField]
    [Tooltip("The material that can blend the pixelation color texture and depth texture into the rendered scene")]
    private Material sceneBlendMaterial;

    [SerializeField]
    [Tooltip("The material is responsible for copying the final scene texture to the screen")]
    private Material blitMaterial;

    [SerializeField]
    [Tooltip("The pixelation resolution to use for the pixelation downscaling")]
    private Vector2Int pixelationResolution = new Vector2Int(480, 270);

    private RenderObjectsPass renderSceneDepthTexturePass;
    private RenderObjectsPass renderSceneNormalsPass;
    private RenderObjectsPass renderScenePass;
    // private RenderObjectsPass compileScenePass;
    private BlitToScreenPass blitToScreenPass;

    public override void Create()
    {
        // Render textures

        var depthBufferHandle = new SharedHandle(() =>
            RTHandles.Alloc(
                scaleFactor: Vector2.one,
                dimension: TextureDimension.Tex2D,
                name: "_ScreenSpacePixelationDepthTexture")
        );

        var normalsBufferHandle = new SharedHandle(() =>
            RTHandles.Alloc(
                scaleFactor: Vector2.one,
                dimension: TextureDimension.Tex2D,
                name:"_ScreenSpacePixelationNormalsTexture")
        );

        var sceneTextureHandle = new SharedHandle(() =>
            RTHandles.Alloc(
                scaleFactor: Vector2.one,
                dimension: TextureDimension.Tex2D,
                name:"_ScreenSpaceSceneTexture")
        );

        // Render passes

        this.renderSceneDepthTexturePass = new (depthBufferHandle, new RenderPassSettings {
            renderPassEvent = renderPassEvent,
            layerMask = pixelationLayers,
            depthBits = DepthBits.Depth32,
            blitMaterial = depthRenderingMaterial,
            // overrideResolution = pixelationResolution,
            onPreBlit = (Material mat, ref RenderingData renderingData) =>
            {
                mat.SetFloat("_Far_Clipping_Distance", renderingData.cameraData.camera.farClipPlane);
            }
        });

        this.renderSceneNormalsPass = new (normalsBufferHandle, new RenderPassSettings {
            renderPassEvent = renderPassEvent,
            layerMask = pixelationLayers,
            overrideMaterial = normalsRenderingMaterial,
            // overrideResolution = pixelationResolution,
        });

        this.renderScenePass = new (sceneTextureHandle, new RenderPassSettings {
            renderPassEvent = renderPassEvent,
            layerMask = pixelationLayers,
            overrideResolution = pixelationResolution,
            blitMaterial = sceneBlendMaterial,
            onPreBlit = (Material mat, ref RenderingData renderingData) =>
            {
                mat.SetTexture("_Scene_Depth_Texture", depthBufferHandle.handle.rt);
                mat.SetTexture("_Scene_Normals_Texture", normalsBufferHandle.handle.rt);
                mat.SetVector("_Outline_Resolution", new Vector2(pixelationResolution.x, pixelationResolution.y));   
            }
        });

        this.blitToScreenPass = new (sceneTextureHandle, blitMaterial)
        {
            renderPassEvent = this.renderPassEvent
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(renderSceneDepthTexturePass);
        renderer.EnqueuePass(renderSceneNormalsPass);
        renderer.EnqueuePass(renderScenePass);
        renderer.EnqueuePass(blitToScreenPass);
    }
}
