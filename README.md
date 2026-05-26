# APV GI Hijack — Unity 6 / URP

A tiny proof-of-concept that injects **realtime** data into Unity's
Adaptive Probe Volume system — originally built only for *baked*
light probes.

## What it does

A `ScriptableRendererFeature` builds the brick index buffer, cell
indirection, and SH pool textures that APV expects, pushes the
`ShaderVariablesProbeVolumes` constant buffer, and enables
`PROBE_VOLUMES_L1`. A compute shader fills the pool each frame with
a world-space rainbow ring (radiating from the camera) modulated by
a sliding value-noise field.

Every APV-aware URP/Lit material then samples your data — no
baking, no scene-authored probe volumes.

## Why care

Validates that realtime data survives APV's encode / index /
CBUFFER pipeline end-to-end. Swap the rainbow producer for your
own (cloud-attenuated sky SH, ground-bounce SH, light-injection
cache, surfel feedback…) and you inherit APV's sampling, leak
reduction, and URP/Lit integration for free.

## Setup

1. Open in Unity **6000.0**, **6000.3**, or **6000.4** (URP 17.x).
2. URP Asset → Lighting → Light Probe System → **Adaptive Probe Volumes**
   (already set in the bundled `PC_RPAsset`).
3. Drop `APVGIFeature` onto your URP Renderer.
4. Add a couple of primitives, press Play.

## Knobs (feature inspector)

- `probeDensity`, `coverageDistance` — clipmap geometry.
- `hueScale`, `hueSpeed`, `intensity` — radial rainbow rings.
- `noiseScale`, `noiseDrift`, `noiseAmount` — sliding noise overlay.

## Caveats

- The brick-index / cell-metadata / pool-packing math is
  reverse-engineered from HDRP's
  `ProbeReferenceVolume.BindAPVRuntimeResources`. **It may not be
  100 % correct** — leak reduction, validity bias, and per-cascade
  `bricksPerAxis` transitions could have subtle bugs.
- `APVGIPass` mixes buffer construction, compute dispatch, and
  shader-global plumbing in one file. It wants a refactor. PRs
  welcome.
