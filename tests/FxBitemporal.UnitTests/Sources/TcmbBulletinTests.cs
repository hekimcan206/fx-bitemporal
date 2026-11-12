using System.Net;
using System.Text;
using FxBitemporal.Core;
using FxBitemporal.Sources;
using FxBitemporal.Sources.Fixtures;
using FxBitemporal.Sources.Tcmb;

namespace FxBitemporal.UnitTests.Sources;

public sealed class TcmbBulletinTests
{
    private static readonly DateOnly Friday = new(2026, 10, 30);

    [Fact]
    public void A_recorded_bulletin_parses_to_known_values()
    {
        var bulletin = RecordedBulletinSource.Load(Friday);

        bulletin.Date.Should().Be(Friday);
        bulletin.BulletinNo.Should().Be("2026/204");
        bulletin.Rates.Should().HaveCount(22);
        var usd = bulletin.Rates.Single(r => r.Currency == "USD");
        usd.ForexBuying.Should().Be(49.7747m);
        usd.Unit.Should().Be(1);
        bulletin.Rates.Single(r => r.Currency == "JPY").Unit.Should().Be(100);

        var sdr = bulletin.Rates.Single(r => r.Currency == "XDR");
        sdr.ForexBuying.Should().NotBeNull();
        (sdr.ForexSelling, sdr.BanknoteBuying, sdr.BanknoteSelling).Should().Be(((decimal?)null, (decimal?)null, (decimal?)null));
    }

    [Fact]
    public void Observations_use_EVDS_series_codes_and_skip_missing_quotes()
    {
        var observations = RecordedBulletinSource.Load(Friday).ToObservations(RateSources.Fixture).ToList();

        observations.Should().Contain(o => o.Series.Code == "TP.DK.USD.A.YTL" && o.Value == 49.7747m);
        observations.Should().Contain(o => o.Series.Code == "TP.DK.JPY.S.EF.YTL" && o.Unit == 100);
        observations.Should().ContainSingle(o => o.Series.Currency == "XDR");
        observations.Should().OnlyContain(o => o.Date == Friday && o.Source == RateSources.Fixture);
    }

    /// <summary>
    /// Golden test over every recorded bulletin: any change in parsing (a dropped currency, a unit mix-up, a rounding
    /// slip) shows up as a diff of a reviewable text file. Regenerate with UPDATE_GOLDEN=1 and review the diff.
    /// </summary>
    [Fact]
    public void All_recorded_bulletins_match_the_golden_observation_file()
    {
        var lines = RecordedBulletinSource.Dates
            .SelectMany(d => RecordedBulletinSource.Load(d).ToObservations(RateSources.Fixture))
            .Select(o => $"{Canonical.Date(o.Date)} {o.Series.Code} {Canonical.Number(o.Value)} /{o.Unit}");
        var actual = string.Join('\n', lines) + "\n";

        Golden.Verify("tcmb-observations.txt", actual);
        RecordedBulletinSource.Dates.Should().HaveCount(28);
    }

    [Fact]
    public void A_DTD_is_rejected_so_entity_expansion_cannot_run()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;">]>
            <Tarih_Date Tarih="30.10.2026" Bulten_No="x"><Currency CurrencyCode="USD"><Unit>1</Unit><ForexBuying>&lol2;</ForexBuying></Currency></Tarih_Date>
            """;

        var act = () => TcmbBulletinParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        act.Should().Throw<RateSourceException>().WithMessage("*not allowed XML*");
    }

    [Theory]
    [InlineData("<rates/>", "*root element*")]
    [InlineData("<Tarih_Date Tarih=\"2026-10-30\"/>", "*Tarih*")]
    [InlineData("<Tarih_Date Tarih=\"30.10.2026\"/>", "*no currencies*")]
    [InlineData("<Tarih_Date Tarih=\"30.10.2026\"><Currency CurrencyCode=\"US\"><Unit>1</Unit></Currency></Tarih_Date>", "*currency code*")]
    [InlineData("<Tarih_Date Tarih=\"30.10.2026\"><Currency CurrencyCode=\"USD\"><Unit>0</Unit></Currency></Tarih_Date>", "*Unit*")]
    [InlineData("<Tarih_Date Tarih=\"30.10.2026\"><Currency CurrencyCode=\"USD\"><Unit>1</Unit><ForexBuying>-1</ForexBuying></Currency></Tarih_Date>", "*positive decimal*")]
    [InlineData("<Tarih_Date Tarih=\"30.10.2026\"><Currency CurrencyCode=\"USD\"><Unit>1</Unit><ForexBuying>4,5</ForexBuying></Currency></Tarih_Date>", "*positive decimal*")]
    [InlineData("<Tarih_Date Tarih=\"30.10.2026\"><Currency CurrencyCode=\"USD\"><Unit>1</Unit></Currency><Currency CurrencyCode=\"USD\"><Unit>1</Unit></Currency></Tarih_Date>", "*twice*")]
    public void Malformed_bulletins_are_rejected_with_a_reason(string xml, string message)
    {
        var act = () => TcmbBulletinParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        act.Should().Throw<RateSourceException>().WithMessage(message);
    }

    [Fact]
    public async Task The_archive_source_requests_dated_paths_skips_weekends_and_treats_404_as_no_bulletin()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/kurlar/202610/30102026.xml" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(RecordedBulletinSource.Open(Friday)) },
            _ => StubHandler.Status(HttpStatusCode.NotFound),
        });
        var source = new TcmbArchiveSource(new HttpClient(handler) { BaseAddress = TcmbArchiveSource.DefaultBaseAddress });

        var observations = await ToListAsync(source.FetchAsync(new DateOnly(2026, 10, 29), new DateOnly(2026, 11, 2)));

        handler.Requests.Select(r => r.RequestUri!.AbsolutePath).Should().Equal(
            "/kurlar/202610/29102026.xml", "/kurlar/202610/30102026.xml", "/kurlar/202611/02112026.xml");
        observations.Should().OnlyContain(o => o.Date == Friday && o.Source == RateSources.TcmbArchive);
        observations.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_archive_source_rejects_a_bulletin_for_another_date()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(RecordedBulletinSource.Open(Friday)) });
        var source = new TcmbArchiveSource(new HttpClient(handler) { BaseAddress = TcmbArchiveSource.DefaultBaseAddress });

        var act = () => source.GetBulletinAsync(new DateOnly(2026, 11, 2));

        await act.Should().ThrowAsync<RateSourceException>().WithMessage("*received 2026-10-30*");
    }

    [Fact]
    public async Task The_archive_source_surfaces_server_errors_and_bounds_the_range()
    {
        var handler = new StubHandler(_ => StubHandler.Status(HttpStatusCode.BadGateway));
        var source = new TcmbArchiveSource(new HttpClient(handler) { BaseAddress = TcmbArchiveSource.DefaultBaseAddress });

        await FluentActions.Awaiting(() => source.GetBulletinAsync(Friday)).Should().ThrowAsync<RateSourceException>().WithMessage("*502*");
        await FluentActions.Awaiting(() => ToListAsync(source.FetchAsync(Friday, Friday.AddDays(TcmbArchiveSource.MaxDays))))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task The_recorded_source_filters_by_date()
    {
        var observations = await ToListAsync(new RecordedBulletinSource().FetchAsync(new DateOnly(2026, 10, 30), new DateOnly(2026, 11, 2)));

        observations.Select(o => o.Date).Distinct().Should().Equal(new DateOnly(2026, 10, 30), new DateOnly(2026, 11, 2));
    }

    internal static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
