namespace BenchForge.Core.Scoring;

/// <summary>
/// All BenchForge scores are linear: twice the performance = twice the score.
/// 1000 points = the documented reference result for that test (see <see cref="ScoreReferences"/>).
/// Combined scores use the geometric mean so no single sub-test dominates.
/// </summary>
public static class Score
{
    public static double Linear(double measured, double reference, bool higherIsBetter = true)
    {
        if (measured <= 0 || reference <= 0 || double.IsNaN(measured)) return 0;
        return higherIsBetter ? 1000.0 * measured / reference : 1000.0 * reference / measured;
    }

    public static double GeoMean(IEnumerable<double> values)
    {
        var v = values.Where(x => x > 0 && !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
        if (v.Count == 0) return 0;
        return Math.Exp(v.Average(Math.Log));
    }

    public static string Format(double score) => score >= 100 ? Math.Round(score).ToString("N0") : score.ToString("N1");
}

/// <summary>
/// Reference values that equal 1000 points. Changing these rescales every score, so they are fixed per format version.
///  - CPU: one 2020s desktop/server core ≈ 1000 single-thread; multi-thread reference = an 8-thread desktop CPU.
///  - RAM: dual-channel DDR4-3200 class system.
///  - Storage: a typical SATA SSD.
///  - GPU: a mid-range 2016-era card (GTX 1060 class) at the test's internal resolution.
/// </summary>
public static class ScoreReferences
{
    // CPU (per kernel, units match CpuKernel.Unit). Single-thread reference values.
    public static readonly Dictionary<string, double> CpuSingle = new()
    {
        // Calibrated so a 2.8 GHz Xeon (cloud) core scores ~600, i.e. 1000 ≈ a current ~5 GHz desktop core.
        ["Integer (prime sieve)"] = 490,       // M numbers/s
        ["Floating point (n-body)"] = 160,     // M interactions/s
        ["Vector FMA (matrix)"] = 43,          // GFLOP/s
        ["Crypto (AES-GCM + SHA-256)"] = 1050, // MB/s
        ["Compression (Brotli)"] = 80,         // MB/s
        ["Sorting"] = 20,                      // M elements/s
    };

    /// <summary>Multi-thread reference = single-thread reference × this factor (8 threads with SMT ≈ 5.5 cores of work).</summary>
    public const double CpuMultiFactor = 5.5;

    // RAM
    public const double MemReadMBs = 32000;
    public const double MemWriteMBs = 22000;
    public const double MemCopyMBs = 36000;  // read + write traffic
    public const double MemLatencyNs = 85;   // lower is better

    // Storage (MB/s)
    public const double DiskSeqRead = 530;
    public const double DiskSeqWrite = 480;
    public const double Disk4kReadQ1 = 40;
    public const double Disk4kWriteQ1 = 100;
    public const double Disk4kReadMT = 320;
    public const double Disk4kWriteMT = 260;

    // GPU: average FPS at the fixed 1920×1080 internal resolution that a GTX 1060-class card is estimated to reach
    // (derived from each scene's triangle / shader / bandwidth cost). Scores stay linear: 2× the FPS = 2× the points.
    public static readonly Dictionary<string, double> Gpu = new()
    {
        ["Geometry"] = 50,     // ~46 M lit triangles per frame
        ["Shading"] = 18,      // ray-marched terrain, ~100k FLOP per pixel
        ["Compute"] = 250,     // 1 M particles × 4 sim steps + additive point render
        ["Fill-rate"] = 250,   // 40 blended full-screen layers, 4 texture taps each
    };
}
