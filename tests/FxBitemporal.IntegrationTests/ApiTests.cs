using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FxBitemporal.Core;
using FxBitemporal.IntegrationTests.Infrastructure;

namespace FxBitemporal.IntegrationTests;

/// <summary>The HTTP contract, end to end, against the service connected as the least-privilege role.</summary>
public sealed class ApiTests(FxEnvironment env)
{
    private static readonly object[] Positions =
    [
        new { currency = "USD", amount = 125000m },
        new { currency = "EUR", amount = -40000.50m },
        new { currency = "JPY", amount = 3000000m },
    ];

    [Fact]
    public async Task The_rate_endpoint_answers_as_of_a_pinned_known_time()
    {
        using var client = env.Client();

        var response = await client.GetAsync(Url($"api/v1/rates/TP.DK.USD.A.YTL?date=2026-10-31&knownAt={Q(env.FixturesKnownAt)}"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("version").GetProperty("value").GetString().Should().Be("49.7747");
        body.GetProperty("version").GetProperty("bulletinDate").GetString().Should().Be("2026-10-30");
        body.GetProperty("knownAt").GetString().Should().Be(Canonical.Timestamp(env.FixturesKnownAt));
        response.Headers.GetValues("X-Known-At").Single().Should().Be(Canonical.Timestamp(env.FixturesKnownAt));
    }

    [Fact]
    public async Task The_report_body_hashes_to_its_header_and_survives_a_correction_byte_for_byte()
    {
        using var client = env.Client();
        using var admin = env.Admin();

        var first = await PostReportAsync(client, knownAt: null);
        var knownAt = first.Headers.GetValues("X-Known-At").Single();
        var firstBytes = await first.Content.ReadAsByteArrayAsync();
        Convert.ToHexStringLower(SHA256.HashData(firstBytes)).Should().Be(first.Headers.GetValues("X-Report-Sha256").Single());

        var correction = await admin.PostAsJsonAsync(Url("api/v1/admin/corrections"), new
        {
            series = "TP.DK.EUR.A.YTL",
            bulletinDate = "2026-10-30",
            value = 55.9149m,
            reason = "synthetic: test correction of the 30 October EUR bulletin",
        });
        correction.StatusCode.Should().Be(HttpStatusCode.OK);
        (await correction.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changed").GetBoolean().Should().BeTrue();

        var replay = await PostReportAsync(client, knownAt);
        var now = await PostReportAsync(client, knownAt: null);

        (await replay.Content.ReadAsByteArrayAsync()).Should().Equal(firstBytes, "same request, same known time: same bytes");
        now.Headers.GetValues("X-Report-Sha256").Single().Should().NotBe(first.Headers.GetValues("X-Report-Sha256").Single());
        var eur = (await now.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("currency").GetString() == "EUR");
        eur.GetProperty("rate").GetString().Should().Be("55.9149");

        var changes = await client.GetFromJsonAsync<JsonElement>(
            Url($"api/v1/rates/TP.DK.EUR.A.YTL/changes?from=2026-10-28&to=2026-11-03&knownA={Uri.EscapeDataString(knownAt)}"));
        changes.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("date").GetString())
            .Should().Equal("2026-10-30", "2026-10-31", "2026-11-01");

        var history = await client.GetFromJsonAsync<JsonElement>(Url("api/v1/rates/TP.DK.EUR.A.YTL/history?date=2026-10-31"));
        history.GetProperty("versions").EnumerateArray().Last().GetProperty("source").GetString().Should().Be("correction");
    }

    [Fact]
    public async Task Admin_endpoints_require_the_key()
    {
        using var client = env.Client();
        client.DefaultRequestHeaders.Add("X-Admin-Key", "wrong");

        var response = await client.PostAsJsonAsync(Url("api/v1/admin/ingest"), new { from = "2026-10-01", to = "2026-10-02" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ingesting_the_recorded_bulletins_again_through_the_api_is_idempotent()
    {
        using var admin = env.Admin();

        var response = await admin.PostAsJsonAsync(Url("api/v1/admin/ingest"), new { from = "2026-10-01", to = "2026-11-11", source = "fixture" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await response.Content.ReadFromJsonAsync<JsonElement>();
        summary.GetProperty("bulletins").GetInt32().Should().Be(28);
        summary.GetProperty("changedBulletins").GetInt32().Should().Be(0);
    }

    [Theory]
    [InlineData("api/v1/rates/USD?date=2026-10-30", HttpStatusCode.BadRequest)]
    [InlineData("api/v1/rates/TP.DK.USD.A.YTL?date=2026-09-01", HttpStatusCode.NotFound)]
    [InlineData("api/v1/rates/TP.DK.USD.A.YTL?date=2026-10-30&knownAt=2999-01-01T00:00:00Z", HttpStatusCode.BadRequest)]
    [InlineData("api/v1/rates/TP.DK.USD.A.YTL?date=not-a-date", HttpStatusCode.BadRequest)]
    [InlineData("api/v1/rates/TP.DK.USD.A.YTL/changes?from=2026-11-01&to=2026-10-01&knownA=2026-11-01T00:00:00Z", HttpStatusCode.BadRequest)]
    public async Task Bad_queries_get_problem_details(string url, HttpStatusCode expected)
    {
        using var client = env.Client();

        var response = await client.GetAsync(Url(url));

        response.StatusCode.Should().Be(expected);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Invalid_reports_and_corrections_are_rejected_with_field_errors()
    {
        using var client = env.Client();
        using var admin = env.Admin();

        var report = await client.PostAsJsonAsync(Url("api/v1/reports/revaluation"),
            new { valuationDate = "2026-10-31", positions = new[] { new { currency = "TRY", amount = 1m } } });
        var correction = await admin.PostAsJsonAsync(Url("api/v1/admin/corrections"),
            new { series = "TP.DK.USD.A.YTL", bulletinDate = "2026-10-30", value = 50m, reason = "short" });

        report.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await report.Content.ReadAsStringAsync()).Should().Contain("positions.currency");
        correction.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await correction.Content.ReadAsStringAsync()).Should().Contain("reason");
    }

    private static async Task<HttpResponseMessage> PostReportAsync(HttpClient client, string? knownAt)
    {
        var response = await client.PostAsJsonAsync(Url("api/v1/reports/revaluation"), new { valuationDate = "2026-10-31", knownAt, positions = Positions });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return response;
    }

    private static Uri Url(string relative) => new(relative, UriKind.Relative);

    private static string Q(DateTimeOffset instant) => Uri.EscapeDataString(Canonical.Timestamp(instant));
}
