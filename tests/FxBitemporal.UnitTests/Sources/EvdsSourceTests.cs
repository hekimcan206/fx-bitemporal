using System.Net;
using FxBitemporal.Core;
using FxBitemporal.Sources;
using FxBitemporal.Sources.Evds;
using FxBitemporal.Sources.Fixtures;

namespace FxBitemporal.UnitTests.Sources;

public sealed class EvdsSourceTests
{
    private const string FixtureName = "usd-eur-2026-10-01_05.json";
    private static readonly SeriesKey Usd = SeriesKey.Parse("TP.DK.USD.A.YTL");
    private static readonly SeriesKey Eur = SeriesKey.Parse("TP.DK.EUR.A.YTL");

    private static FileStream Fixture() => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "evds", FixtureName));

    private static (EvdsSource Source, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond, string key = "test-key-not-real")
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = EvdsSource.DefaultBaseAddress };
        return (new EvdsSource(http, key, [Usd, Eur]), handler);
    }

    [Fact]
    public async Task The_key_travels_in_a_header_never_in_the_url()
    {
        var (source, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Fixture()) });

        await TcmbBulletinTests.ToListAsync(source.FetchAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5)));

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Headers.GetValues("key").Should().Equal("test-key-not-real");
        request.RequestUri!.ToString().Should().NotContain("test-key-not-real");
        request.RequestUri!.ToString().Should().Be(
            "https://evds3.tcmb.gov.tr/igmevdsms-dis/series=TP.DK.USD.A.YTL-TP.DK.EUR.A.YTL&startDate=01-10-2026&endDate=05-10-2026&type=json");
    }

    [Fact]
    public async Task Weekend_nulls_are_skipped_and_values_agree_with_the_recorded_bulletins()
    {
        var (source, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Fixture()) });

        var observations = await TcmbBulletinTests.ToListAsync(source.FetchAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5)));

        observations.Should().HaveCount(6);
        observations.Select(o => o.Date).Distinct().Should().Equal(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 5));
        foreach (var observation in observations)
        {
            var bulletin = RecordedBulletinSource.Load(observation.Date).ToObservations(RateSources.Fixture);
            bulletin.Single(b => b.Series == observation.Series).Value.Should().Be(observation.Value);
        }
    }

    [Fact]
    public async Task A_rejected_key_is_reported_without_echoing_it()
    {
        var (source, _) = Create(_ => StubHandler.Status(HttpStatusCode.Forbidden, """{"status":"403","message":"Required request header 'key' is not present"}"""));

        var act = () => TcmbBulletinTests.ToListAsync(source.FetchAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5)));

        (await act.Should().ThrowAsync<RateSourceException>()).Which.Message.Should().Contain("403").And.NotContain("test-key-not-real");
    }

    [Fact]
    public async Task Without_a_key_no_request_is_made()
    {
        var (source, handler) = Create(_ => StubHandler.Status(HttpStatusCode.OK), key: " ");

        var act = () => TcmbBulletinTests.ToListAsync(source.FetchAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5)));

        await act.Should().ThrowAsync<RateSourceException>().WithMessage("*API key*");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("not json", "*not JSON*")]
    [InlineData("""{"totalCount":0}""", "*items*")]
    [InlineData("""{"items":[{"Tarih":"2026-10-01","TP_DK_USD_A_YTL":"1"}]}""", "*Tarih*")]
    [InlineData("""{"items":[{"Tarih":"01-10-2026","TP_DK_USD_A_YTL":"abc"}]}""", "*not a decimal*")]
    [InlineData("""{"items":[{"Tarih":"01-10-2026","TP_DK_USD_A_YTL":0}]}""", "*not positive*")]
    public async Task Malformed_responses_are_rejected(string body, string message)
    {
        var act = () => EvdsResponseParser.ParseAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)), [Usd], RateSources.Evds);

        await act.Should().ThrowAsync<RateSourceException>().WithMessage(message);
    }

    [Fact]
    public async Task Numeric_cells_and_JPY_units_are_handled()
    {
        var body = """{"items":[{"Tarih":"01-10-2026","TP_DK_JPY_A_YTL":32.5}]}""";

        var result = await EvdsResponseParser.ParseAsync(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)), [SeriesKey.Parse("TP.DK.JPY.A.YTL")], RateSources.Evds);

        result.Should().ContainSingle().Which.Should().Be(
            new Observation(SeriesKey.Parse("TP.DK.JPY.A.YTL"), new DateOnly(2026, 10, 1), 32.5m, 100, RateSources.Evds));
    }
}
