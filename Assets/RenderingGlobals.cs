using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class RenderingGlobals : MonoBehaviour
{
    [System.Serializable]
    private struct TextureSize
    {
        public int width;
        public int height;
    }

    [SerializeField]
    private Camera objectCamera;

    [SerializeField]
    private Camera depthCamera;

    [SerializeField]
    private RawImage depthImage;

    [SerializeField]
    private TextureSize textureSize = new TextureSize
    {
        width = 480,
        height = 270
    };

    [SerializeField]
    private RenderTexture colorTexture;

    [SerializeField]
    private RenderTexture depthTexture;

    private void Awake()
    {
        #if !UNITY_EDITOR
            Destroy(colorTexture);
            Destroy(depthTexture);
            colorTexture = null;
            depthTexture = null;
        #endif

        SetupShaderGlobals();
    }

    private void OnValidate() => SetupShaderGlobals();

    private void SetupShaderGlobals()
    {
        if (objectCamera == null || depthImage == null)
            return;

        objectCamera.depthTextureMode = DepthTextureMode.Depth;
        if (colorTexture == null || depthTexture == null)
        {
                colorTexture = new RenderTexture(textureSize.width, textureSize.height, 24, RenderTextureFormat.Default);
                colorTexture.filterMode = FilterMode.Point;

                depthTexture = new RenderTexture(textureSize.width, textureSize.height, 0, RenderTextureFormat.Default);
                depthTexture.filterMode = FilterMode.Point;

                objectCamera.targetTexture = colorTexture;
                depthCamera.SetTargetBuffers(depthTexture.colorBuffer, colorTexture.depthBuffer);

                #if UNITY_EDITOR
                AssetDatabase.CreateAsset(colorTexture, "Assets/_ColorTexture.renderTexture");
                AssetDatabase.CreateAsset(depthTexture, "Assets/_DepthTexture.renderTexture");
                #endif
        }
    }
}
