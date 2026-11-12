using FxBitemporal.Core;

namespace FxBitemporal.Api.Endpoints;

/// <summary>One version, flattened for JSON. Instants are canonical UTC strings with microseconds.</summary>
public sealed record VersionResponse(
    long VersionId,
    string Series,
    string Value,
    int Unit,
    string BulletinDate,
    string ValidFrom,
    string? ValidTo,
    string KnownFrom,
    string? KnownTo,
    string Source,
    string? Reason)
{
    public static VersionResponse From(RateVersion v) => new(
        v.Id,
        v.Series.Code,
        Canonical.Number(v.Value),
        v.Unit,
        Canonical.Date(v.Fact.ObservedOn),
        Canonical.Date(v.Valid.From),
        v.Valid.To is { } to ? Canonical.Date(to) : null,
        Canonical.Timestamp(v.Known.From),
        v.Known.To is { } kt ? Canonical.Timestamp(kt) : null,
        v.Fact.Source,
        v.Fact.Reason);
}

/// <summary>The answer to "which rate applied on Date, as known at KnownAt?" plus the version it came from.</summary>
public sealed record RateResponse(string Series, string Date, string KnownAt, VersionResponse Version);

public sealed record HistoryResponse(string Series, string Date, IReadOnlyList<VersionResponse> Versions);

public sealed record ChangeResponse(string Date, VersionResponse? Before, VersionResponse? After);

public sealed record ChangesResponse(string Series, string From, string To, string KnownA, string KnownB, IReadOnlyList<ChangeResponse> Changes);

public sealed record RevaluationBody(DateOnly ValuationDate, DateTimeOffset? KnownAt, IReadOnlyList<Core.Reports.Position>? Positions);

public sealed record IngestRequest(DateOnly From, DateOnly To, string? Source);

public sealed record CorrectionRequest(string Series, DateOnly BulletinDate, decimal Value, int? Unit, string Reason);

public sealed record CorrectionResponse(string Series, string BulletinDate, string? KnownFrom, int Closed, int Inserted, bool Changed);
