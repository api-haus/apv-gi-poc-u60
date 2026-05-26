using static Unity.Mathematics.math;

namespace APVGI
{
  using System.Runtime.InteropServices;
  using Unity.Mathematics;
  using UnityEngine;
  using UnityEngine.Experimental.Rendering;
  using UnityEngine.Rendering;
  using UnityEngine.Rendering.RenderGraphModule;
  using UnityEngine.Rendering.Universal;

  // APV GI hijack — proof of concept.
  //
  // Unity's Adaptive Probe Volume system was designed to consume the
  // baked-light artifacts emitted by ProbeReferenceVolume. This pass
  // demonstrates that those artifacts are just shader globals: bind
  // your own brick index buffer + cell indirection + SH pool textures,
  // push the ShaderVariablesProbeVolumes constant buffer, and every
  // APV-aware lit shader in URP starts sampling your data instead.
  //
  // No baking, no probe placement, no scene authoring required. The
  // bricks tile a cubic clipmap centred on the camera; a compute
  // shader fills the pool with animated random colours so you can see
  // the indirect lighting come from our buffers, not Unity's bake.
  //
  // Mirrors HDRP's ProbeReferenceVolume.BindAPVRuntimeResources but
  // with our own backing store. The same trick is what lets Zori
  // Atmospherics inject cloud-attenuated sky SH + terrain bounce
  // colour into APV at runtime.
  //
  // Pipeline:
  //   1. Build (index, cellIndices, brick→entry) lookups once per
  //      cascade-geometry change.
  //   2. Allocate the L0/L1Rx/L1G_Ry/L1B_Rz/Validity pool textures.
  //   3. Each frame: dispatch the random-colour compute shader to
  //      write per-probe SH into the pool.
  //   4. Bind all APV shader globals + push the CBUFFER + enable
  //      PROBE_VOLUMES_L1.
  //
  // Required URP asset setting: Lighting → Light Probe System →
  // Adaptive Probe Volumes. Otherwise the URP/Lit shader compiles
  // without the PROBE_VOLUMES_L1 variant and our keyword has no effect.
  sealed class APVGIPass : ScriptableRenderPass
  {
    [StructLayout(LayoutKind.Sequential)]
    struct ProbeVolumesCB
    {
      public float4 _Offset_LayerCount;
      public float4 _MinLoadedCellInEntries_IndirectionEntryDim;
      public float4 _MaxLoadedCellInEntries_RcpIndirectionEntryDim;
      public float4 _PoolDim_MinBrickSize;
      public float4 _RcpPoolDim_XY;
      public float4 _MinEntryPos_Noise;
      public uint4 _EntryCount_X_XY_LeakReduction;
      public float4 _Biases_NormalizationClamp;
      public float4 _FrameIndex_Weights;
      public uint4 _ProbeVolumeLayerMask;
    }

    const int kBrickProbeCount = 4;
    const int kIndexChunkSize = 243;
    const int kMaxCascades = 12;

    // APV shader globals.
    static readonly int s_APVResIndex = Shader.PropertyToID("_APVResIndex");
    static readonly int s_APVResCellIndices = Shader.PropertyToID("_APVResCellIndices");
    static readonly int s_APVResL0_L1Rx = Shader.PropertyToID("_APVResL0_L1Rx");
    static readonly int s_APVResL1G_L1Ry = Shader.PropertyToID("_APVResL1G_L1Ry");
    static readonly int s_APVResL1B_L1Rz = Shader.PropertyToID("_APVResL1B_L1Rz");
    static readonly int s_APVResValidity = Shader.PropertyToID("_APVResValidity");
    static readonly int s_APVResL2_0 = Shader.PropertyToID("_APVResL2_0");
    static readonly int s_APVResL2_1 = Shader.PropertyToID("_APVResL2_1");
    static readonly int s_APVResL2_2 = Shader.PropertyToID("_APVResL2_2");
    static readonly int s_APVResL2_3 = Shader.PropertyToID("_APVResL2_3");
    static readonly int s_APVProbeOcclusion = Shader.PropertyToID("_APVProbeOcclusion");
    static readonly int s_SkyOcclusionTexL0L1 = Shader.PropertyToID("_SkyOcclusionTexL0L1");
    static readonly int s_SkyShadingDirectionIndicesTex =
      Shader.PropertyToID("_SkyShadingDirectionIndicesTex");
    static readonly int s_SkyPrecomputedDirections =
      Shader.PropertyToID("_SkyPrecomputedDirections");
    static readonly int s_AntiLeakData = Shader.PropertyToID("_AntiLeakData");
    static readonly int s_CBShaderID = Shader.PropertyToID("ShaderVariablesProbeVolumes");
    static readonly int s_EnableProbeVolumes = Shader.PropertyToID("_EnableProbeVolumes");
    static readonly GlobalKeyword s_ProbeVolumeL1Keyword =
      GlobalKeyword.Create("PROBE_VOLUMES_L1");

    // Random-colour compute uniforms.
    static readonly int s_OutL0_L1Rx = Shader.PropertyToID("_OutL0_L1Rx");
    static readonly int s_OutL1G_L1Ry = Shader.PropertyToID("_OutL1G_L1Ry");
    static readonly int s_OutL1B_L1Rz = Shader.PropertyToID("_OutL1B_L1Rz");
    static readonly int s_OutValidity = Shader.PropertyToID("_OutValidity");
    static readonly int s_PoolDim = Shader.PropertyToID("_PoolDim");
    static readonly int s_BrickProbeCount = Shader.PropertyToID("_BrickProbeCount");
    static readonly int s_EntriesPerAxis = Shader.PropertyToID("_EntriesPerAxis");
    static readonly int s_BrickEntryXYZ = Shader.PropertyToID("_BrickEntryXYZ");
    static readonly int s_BrickLocal = Shader.PropertyToID("_BrickLocal");
    static readonly int s_GridMin = Shader.PropertyToID("_GridMin");
    static readonly int s_EntryDim = Shader.PropertyToID("_EntryDim");
    static readonly int s_CameraPos = Shader.PropertyToID("_CameraPos");
    static readonly int s_Time = Shader.PropertyToID("_Time");
    static readonly int s_HueScale = Shader.PropertyToID("_HueScale");
    static readonly int s_HueSpeed = Shader.PropertyToID("_HueSpeed");
    static readonly int s_Intensity = Shader.PropertyToID("_Intensity");
    static readonly int s_NoiseScale = Shader.PropertyToID("_NoiseScale");
    static readonly int s_NoiseDrift = Shader.PropertyToID("_NoiseDrift");
    static readonly int s_NoiseAmount = Shader.PropertyToID("_NoiseAmount");
    static readonly int s_SkyColour = Shader.PropertyToID("_SkyColour");
    static readonly int s_GroundColour = Shader.PropertyToID("_GroundColour");
    static readonly int s_HemiAmount = Shader.PropertyToID("_HemiAmount");
    static readonly int s_SwirlColour = Shader.PropertyToID("_SwirlColour");
    static readonly int s_SwirlAxis = Shader.PropertyToID("_SwirlAxis");
    static readonly int s_SwirlAmount = Shader.PropertyToID("_SwirlAmount");

    static Texture3D s_WhiteVolume;

    static Texture3D WhiteVolumeTexture
    {
      get
      {
        if (s_WhiteVolume == null)
        {
          s_WhiteVolume = new Texture3D(1, 1, 1, TextureFormat.RGBA32, false);
          s_WhiteVolume.SetPixel(0, 0, 0, Color.white);
          s_WhiteVolume.Apply(false, true);
        }

        return s_WhiteVolume;
      }
    }

    ComputeShader _writeCS;
    int _writeKernel;

    RenderTexture _poolL0L1Rx;
    RenderTexture _poolL1G_Ry;
    RenderTexture _poolL1B_Rz;
    RenderTexture _poolValidity;
    ComputeBuffer _indexBuffer;
    ComputeBuffer _cellIndicesBuffer;
    ComputeBuffer _emptySkyDirs;
    ComputeBuffer _emptyAntiLeak;
    ComputeBuffer _brickEntryXYZBuffer;
    ComputeBuffer _brickLocalBuffer;

    int _allocKey;
    int _poolDim;
    int _totalBricks;
    int _entriesPerAxis;

    APVGISettings _settings;
    float3 _cameraWorldPos;

    public void Setup(ComputeShader writeCS)
    {
      _writeCS = writeCS;
      _writeKernel = writeCS.FindKernel("CSWriteRandomColors");
    }

    public void SetFrameParams(in APVGISettings settings, float3 cameraWorldPos)
    {
      _settings = settings;
      _cameraWorldPos = cameraWorldPos;
    }

    // Chebyshev distance from grid centre = cascade level.
    static int EntryToCascade(int ex, int ey, int ez, int centre) =>
      max(abs(ex - centre), max(abs(ey - centre), abs(ez - centre)));

    // APV requires bpa * 3^subdiv == bricksPerAxis at every level so each
    // cascade's bricks tile the entry exactly. Only powers of 3 satisfy
    // this: 3 → (3,1), 9 → (9,3,1), 27 → (27,9,3,1).
    static int SnapBricksPerAxis(int v) => v <= 5 ? 3 : v <= 17 ? 9 : 27;

    static int MaxSubdiv(int bricksPerAxis)
    {
      var s = 0;
      var n = bricksPerAxis;
      while (n > 1)
      {
        n /= 3;
        s++;
      }

      return s;
    }

    static int BricksPerAxisAtSubdiv(int bricksPerAxis, int subdiv)
    {
      var n = bricksPerAxis;
      for (var i = 0; i < subdiv && n > 1; i++)
        n /= 3;
      return max(n, 1);
    }

    void EnsureResources(int bricksPerAxis, int cascadeCount)
    {
      bricksPerAxis = SnapBricksPerAxis(bricksPerAxis);
      cascadeCount = clamp(cascadeCount, 1, kMaxCascades);

      var key = (bricksPerAxis * 100) + cascadeCount;
      if (_allocKey == key && _poolL0L1Rx && _poolL0L1Rx.IsCreated())
        return;

      ReleaseResources();

      _entriesPerAxis = (2 * cascadeCount) - 1;
      var centre = cascadeCount - 1;
      var totalEntries = _entriesPerAxis * _entriesPerAxis * _entriesPerAxis;

      // Count bricks per cascade, then lay them out sequentially in the
      // pool. The pool layout is the source of truth APV's lookup uses:
      // _APVResIndex[chunkIdx * 243 + localBrickFlat] → packed pool
      // position + subdiv level.
      var cascadeBrickCount = new int[kMaxCascades];
      for (var ez = 0; ez < _entriesPerAxis; ez++)
      for (var ey = 0; ey < _entriesPerAxis; ey++)
      for (var ex = 0; ex < _entriesPerAxis; ex++)
      {
        var c = EntryToCascade(ex, ey, ez, centre);
        var bpa = BricksPerAxisAtSubdiv(bricksPerAxis, c);
        cascadeBrickCount[c] += bpa * bpa * bpa;
      }

      var cascadeStart = new int[kMaxCascades];
      cascadeStart[0] = 0;
      for (var c = 1; c < kMaxCascades; c++)
        cascadeStart[c] = cascadeStart[c - 1] + cascadeBrickCount[c - 1];
      _totalBricks = cascadeStart[cascadeCount - 1] + cascadeBrickCount[cascadeCount - 1];

      var bricksPerPoolAxis = max((int)ceil(pow(_totalBricks, 1f / 3f)), 2);
      _poolDim = bricksPerPoolAxis * kBrickProbeCount;

      _poolL0L1Rx = CreatePool3D(_poolDim, GraphicsFormat.R16G16B16A16_SFloat, "APVGI_L0_L1Rx");
      _poolL1G_Ry = CreatePool3D(_poolDim, GraphicsFormat.R8G8B8A8_UNorm, "APVGI_L1G_L1Ry");
      _poolL1B_Rz = CreatePool3D(_poolDim, GraphicsFormat.R8G8B8A8_UNorm, "APVGI_L1B_L1Rz");
      _poolValidity = CreatePool3D(_poolDim, GraphicsFormat.R8_UNorm, "APVGI_Validity");

      var maxSubdiv = MaxSubdiv(bricksPerAxis);

      // Pass 1: compute per-entry chunk starts. Entries with bpa>6 need
      // multiple 243-slot chunks; assign sequentially so each entry's
      // full brick range fits.
      var entryChunkStart = new int[totalEntries];
      var nextChunk = 0;
      for (var ez = 0; ez < _entriesPerAxis; ez++)
      for (var ey = 0; ey < _entriesPerAxis; ey++)
      for (var ex = 0; ex < _entriesPerAxis; ex++)
      {
        var entryFlat = ex + (ey * _entriesPerAxis) + (ez * _entriesPerAxis * _entriesPerAxis);
        var cascade = EntryToCascade(ex, ey, ez, centre);
        var subdiv = min(cascade, maxSubdiv);
        var bpa = BricksPerAxisAtSubdiv(bricksPerAxis, subdiv);
        var bricksInEntry = bpa * bpa * bpa;
        entryChunkStart[entryFlat] = nextChunk;
        nextChunk += (bricksInEntry + kIndexChunkSize - 1) / kIndexChunkSize;
      }

      var numChunks = max(nextChunk, 1);
      var indexData = new int[numChunks * kIndexChunkSize];
      for (var i = 0; i < indexData.Length; i++)
        indexData[i] = unchecked((int)0xFFFFFFFF);

      var cellData = new uint3[totalEntries];
      var brickEntryXYZ = new uint[max(_totalBricks, 1)];
      var brickLocal = new uint[max(_totalBricks, 1)];
      var cascadeNextBrick = new int[kMaxCascades];
      System.Array.Copy(cascadeStart, cascadeNextBrick, kMaxCascades);

      // Pass 2: emit per-brick pool positions + entry indirection.
      for (var ez = 0; ez < _entriesPerAxis; ez++)
      for (var ey = 0; ey < _entriesPerAxis; ey++)
      for (var ex = 0; ex < _entriesPerAxis; ex++)
      {
        var entryFlat = ex + (ey * _entriesPerAxis) + (ez * _entriesPerAxis * _entriesPerAxis);
        var cascade = EntryToCascade(ex, ey, ez, centre);
        var subdiv = min(cascade, maxSubdiv);
        var bpa = BricksPerAxisAtSubdiv(bricksPerAxis, subdiv);
        var entryBrickStart = cascadeNextBrick[cascade];
        var chunkIdx = entryChunkStart[entryFlat];
        var packedEntry = (uint)ex | ((uint)ey << 8) | ((uint)ez << 16);

        for (var bz = 0; bz < bpa; bz++)
        for (var by = 0; by < bpa; by++)
        for (var bx = 0; bx < bpa; bx++)
        {
          var brickFlat = (bx * bpa) + by + (bz * bpa * bpa);
          var globalBrickIdx = entryBrickStart + brickFlat;

          var pbz = globalBrickIdx / (bricksPerPoolAxis * bricksPerPoolAxis);
          var rem = globalBrickIdx % (bricksPerPoolAxis * bricksPerPoolAxis);
          var pby = rem / bricksPerPoolAxis;
          var pbx = rem % bricksPerPoolAxis;

          var flatPool =
            (pbz * kBrickProbeCount * _poolDim * _poolDim)
            + (pby * kBrickProbeCount * _poolDim)
            + (pbx * kBrickProbeCount);

          var packed = (flatPool & ((1 << 28) - 1)) | (subdiv << 28);
          var idxInBuffer = (chunkIdx * kIndexChunkSize) + brickFlat;
          if (idxInBuffer < indexData.Length)
            indexData[idxInBuffer] = packed;

          if (globalBrickIdx < brickEntryXYZ.Length)
          {
            brickEntryXYZ[globalBrickIdx] = packedEntry;
            brickLocal[globalBrickIdx] =
              (uint)bx
              | ((uint)by << 5)
              | ((uint)bz << 10)
              | ((uint)bpa << 15);
          }
        }

        var meta0 = ((uint)chunkIdx & 0x1FFFFFFF) | ((uint)subdiv << 29);
        var meta2 = (uint)bpa | ((uint)bpa << 10) | ((uint)bpa << 20);
        cellData[entryFlat] = uint3(meta0, 0u, meta2);

        cascadeNextBrick[cascade] += bpa * bpa * bpa;
      }

      _indexBuffer = new ComputeBuffer(
        indexData.Length, sizeof(int), ComputeBufferType.Structured);
      _indexBuffer.SetData(indexData);

      _cellIndicesBuffer = new ComputeBuffer(totalEntries, 12, ComputeBufferType.Structured);
      _cellIndicesBuffer.SetData(cellData);

      _brickEntryXYZBuffer = new ComputeBuffer(
        brickEntryXYZ.Length, sizeof(uint), ComputeBufferType.Structured);
      _brickEntryXYZBuffer.SetData(brickEntryXYZ);

      _brickLocalBuffer = new ComputeBuffer(
        brickLocal.Length, sizeof(uint), ComputeBufferType.Structured);
      _brickLocalBuffer.SetData(brickLocal);

      _emptySkyDirs ??= new ComputeBuffer(1, 12, ComputeBufferType.Structured);
      _emptyAntiLeak ??= new ComputeBuffer(1, 4, ComputeBufferType.Structured);

      _allocKey = key;
    }

    static RenderTexture CreatePool3D(int dim, GraphicsFormat fmt, string name)
    {
      var desc = new RenderTextureDescriptor(dim, dim, fmt, GraphicsFormat.None)
      {
        dimension = TextureDimension.Tex3D,
        volumeDepth = dim,
        enableRandomWrite = true,
        msaaSamples = 1,
      };
      var rt = new RenderTexture(desc)
      {
        filterMode = FilterMode.Bilinear,
        wrapMode = TextureWrapMode.Clamp,
        name = name,
      };
      rt.Create();
      return rt;
    }

    public void ReleaseResources()
    {
      CoreUtils.Destroy(_poolL0L1Rx);
      CoreUtils.Destroy(_poolL1G_Ry);
      CoreUtils.Destroy(_poolL1B_Rz);
      CoreUtils.Destroy(_poolValidity);
      _indexBuffer?.Release();
      _cellIndicesBuffer?.Release();
      _emptySkyDirs?.Release();
      _emptyAntiLeak?.Release();
      _brickEntryXYZBuffer?.Release();
      _brickLocalBuffer?.Release();
      _poolL0L1Rx = null;
      _poolL1G_Ry = null;
      _poolL1B_Rz = null;
      _poolValidity = null;
      _indexBuffer = null;
      _cellIndicesBuffer = null;
      _emptySkyDirs = null;
      _emptyAntiLeak = null;
      _brickEntryXYZBuffer = null;
      _brickLocalBuffer = null;
      _allocKey = 0;
    }

    class PassData
    {
      public ComputeShader WriteCS;
      public int WriteKernel;
      public int PoolDim;
      public int BricksPerAxis;
      public float BrickSize;
      public int CascadeCount;
      public int EntriesPerAxis;
      public float3 CamPos;
      public float Time;
      public float HueScale;
      public float HueSpeed;
      public float Intensity;
      public float NoiseScale;
      public float NoiseDrift;
      public float NoiseAmount;
      public float4 SkyColour;
      public float4 GroundColour;
      public float HemiAmount;
      public float4 SwirlColour;
      public float3 SwirlAxisSpun;
      public float SwirlAmount;

      public RenderTexture PoolL0L1Rx;
      public RenderTexture PoolL1G_Ry;
      public RenderTexture PoolL1B_Rz;
      public RenderTexture PoolValidity;
      public ComputeBuffer IndexBuffer;
      public ComputeBuffer CellIndicesBuffer;
      public ComputeBuffer BrickEntryXYZBuffer;
      public ComputeBuffer BrickLocalBuffer;
      public ComputeBuffer EmptySkyDirs;
      public ComputeBuffer EmptyAntiLeak;
      public GlobalKeyword ProbeVolumeL1Keyword;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
      var clipmap = APVGIClipmapDerivation.Derive(
        _settings.probeDensity, _settings.coverageDistance);
      EnsureResources(clipmap.BricksPerAxis, clipmap.CascadeCount);

      using var builder = renderGraph.AddUnsafePass<PassData>("APVGI.WriteAndBind", out var data);
      data.WriteCS = _writeCS;
      data.WriteKernel = _writeKernel;
      data.PoolDim = _poolDim;
      data.BricksPerAxis = SnapBricksPerAxis(clipmap.BricksPerAxis);
      data.BrickSize = clipmap.BrickSize;
      data.CascadeCount = clamp(clipmap.CascadeCount, 1, kMaxCascades);
      data.EntriesPerAxis = _entriesPerAxis;
      data.CamPos = _cameraWorldPos;
      data.Time = Application.isPlaying ? Time.time : (float)Time.realtimeSinceStartupAsDouble;
      data.HueScale = _settings.hueScale;
      data.HueSpeed = _settings.hueSpeed;
      data.Intensity = _settings.intensity;
      data.NoiseScale = _settings.noiseScale;
      data.NoiseDrift = _settings.noiseDrift;
      data.NoiseAmount = _settings.noiseAmount;

      // Hemispherical: pass linear-light colours; the encoder will derive
      // L0 = (sky+ground)/2 and L1.y = (sky-ground)/2 per channel.
      data.SkyColour = (Vector4)_settings.skyColour.linear;
      data.GroundColour = (Vector4)_settings.groundColour.linear;
      data.HemiAmount = _settings.hemisphericalAmount;

      // Swirl: normalize the user axis, then spin it around world-up Y so
      // the directional gradient sweeps through every surface — exercises
      // all three L1 axes over time.
      data.SwirlColour = (Vector4)_settings.swirlColour.linear;
      var ax = (float3)(Vector3)_settings.swirlAxis;
      var axLen = length(ax);
      ax = axLen > 1e-5f ? ax / axLen : float3(1f, 0f, 0f);
      var spin = data.Time * _settings.swirlSpinSpeed;
      var c = cos(spin);
      var s = sin(spin);
      data.SwirlAxisSpun = float3(c * ax.x + s * ax.z, ax.y, -s * ax.x + c * ax.z);
      data.SwirlAmount = _settings.swirlAmount;

      data.PoolL0L1Rx = _poolL0L1Rx;
      data.PoolL1G_Ry = _poolL1G_Ry;
      data.PoolL1B_Rz = _poolL1B_Rz;
      data.PoolValidity = _poolValidity;
      data.IndexBuffer = _indexBuffer;
      data.CellIndicesBuffer = _cellIndicesBuffer;
      data.BrickEntryXYZBuffer = _brickEntryXYZBuffer;
      data.BrickLocalBuffer = _brickLocalBuffer;
      data.EmptySkyDirs = _emptySkyDirs;
      data.EmptyAntiLeak = _emptyAntiLeak;
      data.ProbeVolumeL1Keyword = s_ProbeVolumeL1Keyword;

      builder.AllowGlobalStateModification(true);
      builder.AllowPassCulling(false);

      builder.SetRenderFunc(static (PassData d, UnsafeGraphContext ctx) =>
      {
        var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
        ExecutePass(cmd, d);
      });
    }

    static void ExecutePass(CommandBuffer cmd, PassData d)
    {
      var poolDim = d.PoolDim;
      var bricks = d.BricksPerAxis;
      var brickSize = d.BrickSize;
      var entriesPerAxis = d.EntriesPerAxis;

      // Snap the grid origin to the finest-cascade probe spacing so the
      // clipmap doesn't crawl across the floor when the camera moves
      // sub-probe distances.
      var entryDim = bricks * brickSize;
      var halfGrid = entriesPerAxis * entryDim * 0.5f;
      var probeSpacing = brickSize / 3f;
      var snapped = floor(d.CamPos / probeSpacing) * probeSpacing;
      var gridMin = snapped - float3(halfGrid, halfGrid, halfGrid);

      // ----- Dispatch the random-colour compute. -----
      cmd.SetComputeTextureParam(d.WriteCS, d.WriteKernel, s_OutL0_L1Rx, d.PoolL0L1Rx);
      cmd.SetComputeTextureParam(d.WriteCS, d.WriteKernel, s_OutL1G_L1Ry, d.PoolL1G_Ry);
      cmd.SetComputeTextureParam(d.WriteCS, d.WriteKernel, s_OutL1B_L1Rz, d.PoolL1B_Rz);
      cmd.SetComputeTextureParam(d.WriteCS, d.WriteKernel, s_OutValidity, d.PoolValidity);
      cmd.SetComputeBufferParam(d.WriteCS, d.WriteKernel, s_BrickEntryXYZ, d.BrickEntryXYZBuffer);
      cmd.SetComputeBufferParam(d.WriteCS, d.WriteKernel, s_BrickLocal, d.BrickLocalBuffer);
      cmd.SetComputeIntParams(d.WriteCS, s_PoolDim, poolDim, poolDim, poolDim);
      cmd.SetComputeIntParam(d.WriteCS, s_BrickProbeCount, kBrickProbeCount);
      cmd.SetComputeIntParam(d.WriteCS, s_EntriesPerAxis, entriesPerAxis);
      cmd.SetComputeVectorParam(d.WriteCS, s_GridMin, float4(gridMin, 0));
      cmd.SetComputeFloatParam(d.WriteCS, s_EntryDim, entryDim);
      cmd.SetComputeVectorParam(d.WriteCS, s_CameraPos, float4(d.CamPos, 0));
      cmd.SetComputeFloatParam(d.WriteCS, s_Time, d.Time);
      cmd.SetComputeFloatParam(d.WriteCS, s_HueScale, d.HueScale);
      cmd.SetComputeFloatParam(d.WriteCS, s_HueSpeed, d.HueSpeed);
      cmd.SetComputeFloatParam(d.WriteCS, s_Intensity, d.Intensity);
      cmd.SetComputeFloatParam(d.WriteCS, s_NoiseScale, d.NoiseScale);
      cmd.SetComputeFloatParam(d.WriteCS, s_NoiseDrift, d.NoiseDrift);
      cmd.SetComputeFloatParam(d.WriteCS, s_NoiseAmount, d.NoiseAmount);

      cmd.SetComputeVectorParam(d.WriteCS, s_SkyColour, d.SkyColour);
      cmd.SetComputeVectorParam(d.WriteCS, s_GroundColour, d.GroundColour);
      cmd.SetComputeFloatParam(d.WriteCS, s_HemiAmount, d.HemiAmount);
      cmd.SetComputeVectorParam(d.WriteCS, s_SwirlColour, d.SwirlColour);
      cmd.SetComputeVectorParam(d.WriteCS, s_SwirlAxis, float4(d.SwirlAxisSpun, 0f));
      cmd.SetComputeFloatParam(d.WriteCS, s_SwirlAmount, d.SwirlAmount);

      var groups = (poolDim + 3) / 4;
      cmd.DispatchCompute(d.WriteCS, d.WriteKernel, groups, groups, groups);

      // ----- Bind APV globals. The same set HDRP's
      // ProbeReferenceVolume.BindAPVRuntimeResources binds; here we
      // point them at our own resources instead of the baked-light
      // ones the system was designed for. -----
      cmd.SetGlobalBuffer(s_APVResIndex, d.IndexBuffer);
      cmd.SetGlobalBuffer(s_APVResCellIndices, d.CellIndicesBuffer);
      cmd.SetGlobalTexture(s_APVResL0_L1Rx, d.PoolL0L1Rx);
      cmd.SetGlobalTexture(s_APVResL1G_L1Ry, d.PoolL1G_Ry);
      cmd.SetGlobalTexture(s_APVResL1B_L1Rz, d.PoolL1B_Rz);
      cmd.SetGlobalTexture(s_APVResValidity, d.PoolValidity);

      // L2 + sky occlusion + anti-leak stay neutral — keyword is L1.
      cmd.SetGlobalTexture(s_SkyOcclusionTexL0L1, CoreUtils.blackVolumeTexture);
      cmd.SetGlobalTexture(s_SkyShadingDirectionIndicesTex, CoreUtils.blackVolumeTexture);
      cmd.SetGlobalBuffer(s_SkyPrecomputedDirections, d.EmptySkyDirs);
      cmd.SetGlobalBuffer(s_AntiLeakData, d.EmptyAntiLeak);
      cmd.SetGlobalTexture(s_APVResL2_0, CoreUtils.blackVolumeTexture);
      cmd.SetGlobalTexture(s_APVResL2_1, CoreUtils.blackVolumeTexture);
      cmd.SetGlobalTexture(s_APVResL2_2, CoreUtils.blackVolumeTexture);
      cmd.SetGlobalTexture(s_APVResL2_3, CoreUtils.blackVolumeTexture);
      cmd.SetGlobalTexture(s_APVProbeOcclusion, WhiteVolumeTexture);

      var rcpEntryDim = 1f / entryDim;
      var maxEntry = entriesPerAxis - 1;

      var cb = new ProbeVolumesCB
      {
        _Offset_LayerCount = float4(gridMin, 1),
        _MinLoadedCellInEntries_IndirectionEntryDim = float4(0, 0, 0, entryDim),
        _MaxLoadedCellInEntries_RcpIndirectionEntryDim =
          float4(maxEntry, maxEntry, maxEntry, rcpEntryDim),
        _PoolDim_MinBrickSize = float4(poolDim, poolDim, poolDim, brickSize),
        _RcpPoolDim_XY = float4(1f / poolDim, 1f / poolDim, 1f / poolDim, 1f / (poolDim * poolDim)),
        _MinEntryPos_Noise = float4(0, 0, 0, 0),
        _EntryCount_X_XY_LeakReduction =
          uint4((uint)entriesPerAxis, (uint)(entriesPerAxis * entriesPerAxis), 0, 0),
        _Biases_NormalizationClamp = float4(0, 0, 0.005f, 7f),
        _FrameIndex_Weights = float4(0, 1f, 0, 0),
        _ProbeVolumeLayerMask = uint4(0xFFFFFFFF, 0, 0, 0),
      };

      ConstantBuffer.PushGlobal(cmd, cb, s_CBShaderID);

      cmd.SetGlobalInt(s_EnableProbeVolumes, 1);
      cmd.SetKeyword(d.ProbeVolumeL1Keyword, true);
    }
  }
}
