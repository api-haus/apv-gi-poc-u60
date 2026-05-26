namespace APVGI
{
  using System;
  using UnityEngine;

  [Serializable]
  public struct APVGISettings
  {
    [Min(0.05f)]
    [Tooltip(
      "Probes per axial metre at the finest cascade. Higher = denser.\n"
        + "Drives brickSize = 3 / probeDensity. Reasonable range 0.25 - 2."
    )]
    public float probeDensity;

    [Min(1f)]
    [Tooltip(
      "Target GI coverage extent in metres. Derivation grows the cascade\n"
        + "count (and, if needed, bricks-per-axis) until\n"
        + "(2*cascades - 1) * bpa * brickSize ≥ coverageDistance.\n"
        + "Hard ceiling ≈ 621 × brickSize (bpa=27, cascades=12)."
    )]
    public float coverageDistance;

    [Header("Rainbow rings (radiating from camera)")]
    [Min(0f)]
    [Tooltip("Hue cycles per metre along the radial axis.")]
    public float hueScale;

    [Tooltip("Hue drift in cycles per second. Negative reverses direction.")]
    public float hueSpeed;

    [Min(0f)]
    [Tooltip("Overall L0 multiplier.")]
    public float intensity;

    [Header("Sliding noise modulation")]
    [Min(0f)]
    [Tooltip("Inverse spatial wavelength of the noise field (m⁻¹).")]
    public float noiseScale;

    [Tooltip("Noise scroll speed in metres per second (XYZ).")]
    public float noiseDrift;

    [Range(0f, 1f)]
    [Tooltip("0 = pure rainbow rings, 1 = full noise modulation.")]
    public float noiseAmount;

    public static APVGISettings Default => new()
    {
      probeDensity = 1f,
      coverageDistance = 60f,
      hueScale = 0.05f,        // one full rainbow every ~20 m
      hueSpeed = 0.25f,        // a quarter cycle per second
      intensity = 1f,
      noiseScale = 0.08f,      // ~12 m blobs
      noiseDrift = 0.5f,
      noiseAmount = 0.6f,
    };
  }
}
