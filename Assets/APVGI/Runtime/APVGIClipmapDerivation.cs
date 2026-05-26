using static Unity.Mathematics.math;

namespace APVGI
{
  // Picks (bricksPerAxis, cascadeCount, brickSize) from two artist-facing
  // knobs: probeDensity (probes per axial metre) and coverageDistance
  // (target volume extent in metres).
  //
  // Coverage model — all entries are uniform size:
  //   targetBrickSize = 3 / probeDensity
  //   entryDim        = bricksPerAxis * brickSize
  //   reach (extent)  = (2 * cascadeCount - 1) * entryDim
  //
  // Reach takes priority over density: the derivation always returns a
  // clipmap that covers coverageDistance, even if that forces brickSize
  // up past the density target. Hits hardware cap at (27 bpa, 12
  // cascades); past that, brickSize grows to fit reach and density
  // degrades as a side-effect.
  public readonly struct APVGIClipmap
  {
    public readonly int BricksPerAxis;
    public readonly int CascadeCount;
    public readonly float BrickSize;

    public APVGIClipmap(int bricksPerAxis, int cascadeCount, float brickSize)
    {
      BricksPerAxis = bricksPerAxis;
      CascadeCount = cascadeCount;
      BrickSize = brickSize;
    }
  }

  public static class APVGIClipmapDerivation
  {
    const int HardMaxCascades = 12;

    // Only powers of 3 tile the entry grid cleanly. Ordered conservative
    // → expensive so the loop picks the cheapest combo that fills.
    static readonly int[] BrickCandidates = { 3, 9, 27 };

    public static APVGIClipmap Derive(float probeDensity, float coverageDistance)
    {
      var targetBrickSize = 3f / max(probeDensity, 1e-4f);
      foreach (var bpa in BrickCandidates)
        for (var cascades = 1; cascades <= HardMaxCascades; cascades++)
        {
          var coverage = ((2f * cascades) - 1f) * bpa * targetBrickSize;
          if (coverage >= coverageDistance)
            return new APVGIClipmap(bpa, cascades, targetBrickSize);
        }

      // Target density unreachable at (27, 12). Grow brickSize to fit
      // reach at max geometry; density degrades.
      var fittedBrickSize = coverageDistance / (((2f * HardMaxCascades) - 1f) * 27f);
      return new APVGIClipmap(27, HardMaxCascades, fittedBrickSize);
    }
  }
}
