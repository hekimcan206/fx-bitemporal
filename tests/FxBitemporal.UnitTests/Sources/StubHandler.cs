using System.Net;

namespace FxBitemporal.UnitTests.Sources;

/// <summary>Answers HTTP requests from a function and records them, so source clients are tested without a network.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public static HttpResponseMessage Status(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}
