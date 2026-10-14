namespace ImpressionFEA.Core.Simulation;

/// <summary>
/// Remaining-time estimate of a run from (elapsed time, progress fraction) samples. Increments get slower towards die
/// closure (contact, flash, remeshing), so the recent rate (last <see cref="Window"/> samples) is weighted more than
/// the average over the whole run. No estimate before <see cref="MinSamples"/> samples and <see cref="MinFraction"/>.
/// </summary>
public sealed class EtaEstimator
{
    public const int Window = 10;
    public const int MinSamples = 3;
    public const double MinFraction = 0.03;
    private const double RecentWeight = 0.7;

    private readonly List<(double T, double F)> _samples = [];

    public void Reset() => _samples.Clear();

    /// <summary>Adds a sample: seconds since the start and the completed fraction (0–1, non-decreasing).</summary>
    public void Add(double elapsedSeconds, double fraction)
    {
        if (!double.IsFinite(elapsedSeconds) || !double.IsFinite(fraction)) return;
        fraction = Math.Clamp(fraction, 0, 1);
        if (_samples.Count > 0 && (fraction < _samples[^1].F || elapsedSeconds < _samples[^1].T)) return;
        _samples.Add((elapsedSeconds, fraction));
    }

    /// <summary>Elapsed time of the last sample, s.</summary>
    public double LastSampleTime => _samples.Count > 0 ? _samples[^1].T : 0;

    /// <summary>Estimated seconds remaining at the last sample, or null while there is too little data.</summary>
    public double? RemainingSeconds
    {
        get
        {
            if (_samples.Count < MinSamples) return null;
            var (t, f) = _samples[^1];
            if (f < MinFraction || t <= 0) return null;
            if (f >= 1) return 0;
            double perAll = t / f; // seconds per unit fraction, whole run
            var (t0, f0) = _samples[Math.Max(0, _samples.Count - 1 - Window)];
            double perRecent = f > f0 && t > t0 ? (t - t0) / (f - f0) : perAll;
            return (1 - f) * (RecentWeight * perRecent + (1 - RecentWeight) * perAll);
        }
    }

    /// <summary>Estimated seconds remaining at <paramref name="nowSeconds"/> (counts down between samples, never below 0).</summary>
    public double? RemainingAt(double nowSeconds) =>
        RemainingSeconds is { } r ? Math.Max(0, r - Math.Max(0, nowSeconds - LastSampleTime)) : null;
}
