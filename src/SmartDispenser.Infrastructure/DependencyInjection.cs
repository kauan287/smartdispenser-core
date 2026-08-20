using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartDispenser.Core.Interfaces;
using SmartDispenser.Infrastructure.Data;
using SmartDispenser.Infrastructure.ExternalServices;
using SmartDispenser.Infrastructure.Repositories;

namespace SmartDispenser.Infrastructure;

/// <summary>
/// Extension method to register all Infrastructure services.
/// Called once from Program.cs — keeps startup code clean.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── PostgreSQL via EF Core ─────────────────────────────────────────────
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection"),
                npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)));

        // ── Repositories ──────────────────────────────────────────────────────
        services.AddScoped<IDispenserRepository, DispenserRepository>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IDispenseRequestRepository, DispenseRequestRepository>();


        // Note: IDispenserHubContext is registered in Program.cs (API layer)
        // because its implementation depends on DispenserHub and IDispenserClient,
        // which are API-layer types. Infrastructure only defines the interface (Core).

        // ── HealthTech API Client with full resilience pipeline ───────────────
        services.AddHttpClient<IHealthTechApiClient, HealthTechApiClient>(client =>
        {
            client.BaseAddress = new Uri(
                configuration["HealthTechApi:BaseUrl"]
                ?? throw new InvalidOperationException("HealthTechApi:BaseUrl is not configured."));
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.Timeout = TimeSpan.FromSeconds(15); // Global timeout (overrides per-request)
        })
        .AddStandardResilienceHandler(options =>
        {
            // Retry: 3 attempts, exponential backoff starting at 2s
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.Delay = TimeSpan.FromSeconds(2);
            options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;

            // Circuit Breaker: opens after 5 failures within 30s, recovers after 60s
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.MinimumThroughput = 5;
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(60);

            // Per-attempt timeout: 10s (independent of the global client timeout)
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        });

        return services;
    }
}
