using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartDispenser.Core.Interfaces;
using SmartDispenser.Infrastructure.Data;
using SmartDispenser.Infrastructure.ExternalServices;
using SmartDispenser.Infrastructure.Repositories;

namespace SmartDispenser.Infrastructure;

public static class DatabaseProvider
{
    public const string Postgres = "Postgres";
    public const string InMemory = "InMemory";
}

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? DatabaseProvider.Postgres;
        Action<DbContextOptionsBuilder> configureDb = provider switch
        {
            DatabaseProvider.Postgres => o => o.UseNpgsql(configuration.GetConnectionString("DefaultConnection")),
            DatabaseProvider.InMemory => o => o.UseInMemoryDatabase("smartdispenser"),
            _ => throw new InvalidOperationException(
                $"Database:Provider must be '{DatabaseProvider.Postgres}' or '{DatabaseProvider.InMemory}', got '{provider}'."),
        };
        services.AddDbContext<AppDbContext>(configureDb);
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddScoped<IDispenserRepository, DispenserRepository>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IDispenseRequestRepository, DispenseRequestRepository>();

        services.AddHttpClient<IHealthTechApiClient, HealthTechApiClient>(client =>
        {
            client.BaseAddress = new Uri(
                configuration["HealthTechApi:BaseUrl"]
                ?? throw new InvalidOperationException("HealthTechApi:BaseUrl is not configured."));
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);

            options.Retry.MaxRetryAttempts = 3;
            options.Retry.Delay = TimeSpan.FromSeconds(2);
            options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;

            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.MinimumThroughput = 5;
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(60);
        });

        return services;
    }
}
