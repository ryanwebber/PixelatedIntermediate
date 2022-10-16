using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

[RequireComponent(typeof(UniversalAdditionalCameraData))]
public class ForceDepthTexture : MonoBehaviour
{
    private void Start() => SetupCamera();
    private void OnValidate() => SetupCamera();

    private void SetupCamera()
    {
        var cameraData = GetComponent<UniversalAdditionalCameraData>();
        cameraData.requiresDepthOption = CameraOverrideOption.On;
        cameraData.requiresDepthTexture = true;
    }
}
