using FxBitemporal.Core;

namespace FxBitemporal.Sources;

/// <summary>A provider of published rates for a date range, in ascending bulletin-date order.</summary>
public interface IRateSource
{
    string Name { get; }

    IAsyncEnumerable<Observation> FetchAsync(DateOnly from, DateOnly toInclusive, CancellationToken cancellationToken = default);
}

/// <summary>A source answered, but not with what the contract says it should (bad status, malformed body).</summary>
public sealed class RateSourceException : Exception
{
    public RateSourceException()
    {
    }

    public RateSourceException(string message)
        : base(message)
    {
    }

    public RateSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
