using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SmartDispenser.Core.Interfaces;
using SmartDispenser.Infrastructure.ExternalServices;

namespace SmartDispenser.IntegrationTests;

/// <summary>
/// Hosts the API in memory. Runs on the InMemory provider by default, or on PostgreSQL
/// (migrations applied at startup) when TEST_POSTGRES_CONNECTION is set.
/// </summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    public const string WebhookSecret = "local-development-secret-not-for-production";

    public FakeOrchestrator Orchestrator { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        if (Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION") is { } postgres)
        {
            builder.UseSetting("Database:Provider", "Postgres");
            builder.UseSetting("ConnectionStrings:DefaultConnection", postgres);
        }
        builder.ConfigureTestServices(services =>
            services.AddHttpClient<IHealthTechApiClient, HealthTechApiClient>()
                .ConfigurePrimaryHttpMessageHandler(() => Orchestrator));
    }

    public HttpRequestMessage SignedWebhook(string json, string? secret = null)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? WebhookSecret));
        var signature = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(json)));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhook")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Signature-SHA256", $"sha256={signature}");
        return request;
    }
}

/// <summary>Stands in for the HealthTech Agent Orchestrator's prescription endpoint.</summary>
public sealed class FakeOrchestrator : HttpMessageHandler
{
    private readonly Dictionary<string, string> _sessions = new();

    public void Returns(string prescriptionId, string json) => _sessions[$"/api/v1/prescriptions/session-{prescriptionId}"] = json;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(_sessions.TryGetValue(request.RequestUri!.AbsolutePath, out var json)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
}
