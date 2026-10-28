using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SmartDispenser.IntegrationTests;

public sealed class ClientRateLimitTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private const string ClientIpHeader = "CF-Connecting-IP";

    private WebApplicationFactory<Program> WithLimit(string permitsPerMinute) => factory.WithWebHostBuilder(b => b
        .UseSetting("RateLimit:PermitsPerMinute", permitsPerMinute)
        .UseSetting("RateLimit:ClientIpHeader", ClientIpHeader));

    private static Task<HttpResponseMessage> GetAs(HttpClient http, string path, string clientIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(ClientIpHeader, clientIp);
        return http.SendAsync(request);
    }

    [Fact]
    public async Task Fourth_request_in_a_minute_is_rejected_with_429_and_Retry_After()
    {
        await using var app = WithLimit("3");
        var http = app.CreateClient();

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 4; i++)
        {
            last = await GetAs(http, "/api/dispensers", "203.0.113.1");
            statuses.Add(last.StatusCode);
        }

        statuses.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
        last.Headers.GetValues("Retry-After").Should().Equal("60");
    }

    [Fact]
    public async Task Each_client_ip_gets_its_own_budget()
    {
        await using var app = WithLimit("1");
        var http = app.CreateClient();

        var first = await GetAs(http, "/api/dispensers", "203.0.113.1");
        var second = await GetAs(http, "/api/dispensers", "203.0.113.2");
        var firstAgain = await GetAs(http, "/api/dispensers", "203.0.113.1");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        firstAgain.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Health_endpoint_is_never_limited()
    {
        await using var app = WithLimit("1");
        var http = app.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await GetAs(http, "/health/live", "203.0.113.1")).StatusCode);
        var api = await GetAs(http, "/api/dispensers", "203.0.113.1");
        var apiAgain = await GetAs(http, "/api/dispensers", "203.0.113.1");

        statuses.Should().AllBeEquivalentTo(HttpStatusCode.OK);
        api.StatusCode.Should().Be(HttpStatusCode.OK);
        apiAgain.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public void Negative_permits_per_minute_fails_at_startup()
    {
        using var app = WithLimit("-1");

        var start = () => app.CreateClient();

        start.Should().Throw<InvalidOperationException>()
            .WithMessage("RateLimit:PermitsPerMinute must not be negative.");
    }
}
