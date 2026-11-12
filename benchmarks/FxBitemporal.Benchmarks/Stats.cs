using System.Globalization;

namespace FxBitemporal.Benchmarks;

/// <summary>Latency samples in milliseconds, summarised the same way everywhere.</summary>
public sealed record Summary(int Samples, double MedianMs, double P95Ms, double P99Ms, double MinMs, double MaxMs)
{
    public static Summary Of(IReadOnlyCollection<double> samples)
    {
        if (samples.Count == 0)
        {
            return new Summary(0, 0, 0, 0, 0, 0);
        }

        var sorted = samples.Order().ToArray();
        return new Summary(sorted.Length, Round(Percentile(sorted, 0.50)), Round(Percentile(sorted, 0.95)),
            Round(Percentile(sorted, 0.99)), Round(sorted[0]), Round(sorted[^1]));
    }

    // Nearest-rank percentile: always an observed sample, never an interpolated one.
    private static double Percentile(double[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static double Round(double value) => Math.Round(value, 3);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"n={Samples} median={MedianMs:0.000} ms p95={P95Ms:0.000} ms p99={P99Ms:0.000} ms max={MaxMs:0.000} ms");
}

/// <summary>Readers and one writer hammering the same series for a fixed time.</summary>
public sealed record ContentionResult(Summary Reads, Summary Writes, double ReadsPerSecond, double WritesPerSecond);
