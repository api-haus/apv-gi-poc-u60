namespace APVGI
{
  using UnityEngine;
  using UnityEngine.Rendering.Universal;

  // Drop me on the URP Renderer to inject animated random-colour GI
  // into APV. Requires URP Asset → Lighting → Light Probe System set
  // to Adaptive Probe Volumes so URP/Lit compiles the PROBE_VOLUMES_L1
  // variant.
  public sealed class APVGIFeature : ScriptableRendererFeature
  {
    [SerializeField] APVGISettings settings = APVGISettings.Default;
    [SerializeField] ComputeShader randomColoursCS;

    APVGIPass _pass;

    public override void Create()
    {
      if (randomColoursCS == null)
      {
        // Fallback discovery so the feature works after a fresh asset
        // import without the user needing to drag the compute shader in.
        randomColoursCS = Resources.Load<ComputeShader>("APVGIRandomColors");
      }

      _pass ??= new APVGIPass
      {
        // Must run after URP's ForwardLights.SetupLights (which writes
        // _EnableProbeVolumes=0 and may toggle PROBE_VOLUMES_L1 off).
        // BeforeRenderingOpaques sits after SetupLights so our globals
        // are the last word.
        renderPassEvent = RenderPassEvent.BeforeRenderingOpaques,
      };

      if (randomColoursCS != null)
        _pass.Setup(randomColoursCS);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data)
    {
      if (_pass == null || randomColoursCS == null)
        return;
      if (data.cameraData.cameraType != CameraType.Game
        && data.cameraData.cameraType != CameraType.SceneView)
        return;

      _pass.SetFrameParams(in settings, data.cameraData.camera.transform.position);
      renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
      _pass?.ReleaseResources();
      _pass = null;
    }
  }
}
