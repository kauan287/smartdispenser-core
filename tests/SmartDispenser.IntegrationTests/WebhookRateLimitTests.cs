using System.Net;
using FluentAssertions;
using Xunit;

namespace SmartDispenser.IntegrationTests;

public sealed class WebhookRateLimitTests(AppFactory factory) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Burst_of_20_webhooks_in_one_second_is_partly_rejected_with_429()
    {
        var http = factory.CreateClient();
        var body = """{"transactionId":"TX-RL","idempotencyKey":"idem-RL","dispenseRequestId":"not-a-guid","amount":1,"currency":"EUR","status":"confirmed","provider":"sandbox","timestamp":"2026-10-27T12:00:00Z"}""";

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => http.SendAsync(factory.SignedWebhook(body))));

        responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests).Should().Be(5);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(15);
    }
}
