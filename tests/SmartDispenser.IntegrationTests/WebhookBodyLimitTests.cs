using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Xunit;

namespace SmartDispenser.IntegrationTests;

public sealed class KestrelApp : IDisposable
{
    private readonly AppFactory _factory = new();

    public Uri BaseAddress { get; }

    public KestrelApp()
    {
        _factory.UseKestrel(0);
        BaseAddress = _factory.CreateClient().BaseAddress!;
    }

    public void Dispose() => _factory.Dispose();
}

/// <summary>
/// Runs on Kestrel with a plain HttpClient. TestServer and the factory's client both buffer the
/// request body and send a Content-Length, which hides the chunked case.
/// </summary>
public sealed class WebhookBodyLimitTests(KestrelApp app) : IClassFixture<KestrelApp>
{
    [Fact]
    public async Task Signed_chunked_body_larger_than_512_KB_is_rejected_with_413()
    {
        var body = Encoding.UTF8.GetBytes(new string('x', 700 * 1024));
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppFactory.WebhookSecret), body));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhook") { Content = new ChunkedContent(body) };
        request.Headers.Add("X-Signature-SHA256", $"sha256={signature}");

        using var http = new HttpClient { BaseAddress = app.BaseAddress };
        var response = await http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Unsigned_webhook_is_rejected_before_its_body_is_read()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(app.BaseAddress.Host, app.BaseAddress.Port);
        var stream = tcp.GetStream();
        var unfinishedRequest = "POST /api/payments/webhook HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n" +
                                "Transfer-Encoding: chunked\r\n\r\n11\r\n{\"transactionId\":\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(unfinishedRequest));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var statusLine = await new StreamReader(stream, Encoding.ASCII).ReadLineAsync(timeout.Token);

        statusLine.Should().Be("HTTP/1.1 401 Unauthorized");
    }

    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
