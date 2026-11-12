using System.Globalization;
using System.Runtime.CompilerServices;
using FxBitemporal.Core;
using FxBitemporal.Sources.Tcmb;

namespace FxBitemporal.Sources.Fixtures;

/// <summary>
/// Replays real TCMB bulletins recorded from the public archive (October and November 2026, embedded in this
/// assembly). It is the default source of the demo and the tests: no network, no key, identical bytes every run.
/// </summary>
public sealed class RecordedBulletinSource : IRateSource
{
    private const string Prefix = "fixtures/tcmb/";

    public string Name => RateSources.Fixture;

    /// <summary>Bulletin dates available in the recording.</summary>
    public static IReadOnlyList<DateOnly> Dates { get; } = [.. typeof(RecordedBulletinSource).Assembly
        .GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
        .Select(n => DateOnly.ParseExact(n[Prefix.Length..^".xml".Length], "yyyy-MM-dd", CultureInfo.InvariantCulture))
        .Order()];

    public static Stream Open(DateOnly date) =>
        typeof(RecordedBulletinSource).Assembly.GetManifestResourceStream(
            string.Create(CultureInfo.InvariantCulture, $"{Prefix}{date:yyyy-MM-dd}.xml"))
        ?? throw new FileNotFoundException(string.Create(CultureInfo.InvariantCulture, $"No recorded bulletin for {date:yyyy-MM-dd}."));

    public static Bulletin Load(DateOnly date)
    {
        using var stream = Open(date);
        return TcmbBulletinParser.Parse(stream);
    }

    public async IAsyncEnumerable<Observation> FetchAsync(
        DateOnly from, DateOnly toInclusive, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var date in Dates.Where(d => d >= from && d <= toInclusive))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var observation in Load(date).ToObservations(Name))
            {
                yield return observation;
            }
        }

        await Task.CompletedTask;
    }

}
