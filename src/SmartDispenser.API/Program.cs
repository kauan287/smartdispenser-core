using MediatR;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using SmartDispenser.API.BackgroundServices;
using SmartDispenser.API.Hubs;
using SmartDispenser.API.Middleware;
using SmartDispenser.API.SignalR;
using SmartDispenser.Core.Interfaces;
using SmartDispenser.Infrastructure;
using SmartDispenser.Infrastructure.Data;
using System.Threading.RateLimiting;

// ── Serilog: structured logging configured before the host builds ─────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog ───────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, services, config) => config
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // ── Controllers + OpenAPI (native .NET 10, no Swashbuckle needed) ────────────
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddOpenApi(options =>
    {
        options.AddDocumentTransformer((document, context, ct) =>
        {
            document.Info = new()
            {
                Title = "SmartDispenser Core API",
                Version = "v1",
                Description = "Real-time backend for smart pharmaceutical vending machines. " +
                              "Integrates with HealthTech Agent Orchestrator for prescription verification.",
            };
            return Task.CompletedTask;
        });
    });

    // ── MediatR — scans Core assembly for all IRequestHandler<,> ─────────────
    builder.Services.AddMediatR(cfg =>
        cfg.RegisterServicesFromAssembly(typeof(SmartDispenser.Core.Commands.InitiateDispense.InitiateDispenseHandler).Assembly));

    // ── Infrastructure (DB, repositories, HTTP clients, SignalR context) ──────
    builder.Services.AddInfrastructure(builder.Configuration);

    // ── SignalR ───────────────────────────────────────────────────────────────
    builder.Services.AddSignalR(opts =>
    {
        opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
        opts.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        opts.KeepAliveInterval = TimeSpan.FromSeconds(15);
    });

    // IDispenserHubContext implementation lives in API (depends on DispenserHub + IDispenserClient)
    builder.Services.AddScoped<IDispenserHubContext, DispenserHubContext>();

    // ── Blazor Server ─────────────────────────────────────────────────────────
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    // ── Rate Limiting ─────────────────────────────────────────────────────────
    builder.Services.AddRateLimiter(opts =>
    {
        // Webhook endpoint: 10 req/s per IP — prevents replay flooding
        opts.AddSlidingWindowLimiter("webhook", limiterOpts =>
        {
            limiterOpts.PermitLimit = 10;
            limiterOpts.Window = TimeSpan.FromSeconds(1);
            limiterOpts.SegmentsPerWindow = 2;
            limiterOpts.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            limiterOpts.QueueLimit = 5;
        });

        opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });

    // ── Health Checks ─────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<AppDbContext>("database")
        .AddCheck("healthtech-api", () =>
        {
            // Non-blocking liveness: circuit breaker state is already tracked by Polly.
            // For a production setup, add AspNetCore.HealthChecks.Uris NuGet package
            // and use .AddUrlGroup() for full HTTP health probing.
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("HealthTech API configured.");
        }, tags: ["ready"]);

    // ── Background Services ───────────────────────────────────────────────────
    builder.Services.AddHostedService<OutboxProcessor>();
    builder.Services.AddHostedService<HeartbeatMonitor>();

    // ── CORS (dev only) ───────────────────────────────────────────────────────
    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddCors(opts =>
            opts.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
    }

    // ─────────────────────────────────────────────────────────────────────────
    var app = builder.Build();
    // ─────────────────────────────────────────────────────────────────────────

    // Auto-apply EF Core migrations on startup (controlled by config flag)
    if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        Log.Information("Database migrations applied.");
    }

    app.UseSerilogRequestLogging(opts =>
    {
        opts.GetLevel = (ctx, _, _) =>
            ctx.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Debug : LogEventLevel.Information;
    });

    if (app.Environment.IsDevelopment())
    {
        // Native .NET 10 OpenAPI — serves spec at /openapi/v1.json
        app.MapOpenApi();
        app.UseCors();
    }

    app.UseWebhookSignatureValidation(); // HMAC-SHA256 before all other middleware
    app.UseRateLimiter();

    app.UseStaticFiles();
    app.UseAntiforgery();

    app.MapControllers();
    app.MapHub<DispenserHub>("/hubs/dispensers");
    app.MapRazorComponents<SmartDispenser.API.Components.App>()
        .AddInteractiveServerRenderMode();
    app.MapHealthChecks("/health/live");
    app.MapHealthChecks("/health/ready", new()
    {
        Predicate = check => check.Tags.Contains("ready")
    });

    Log.Information("SmartDispenser Core starting on {Env}", app.Environment.EnvironmentName);
    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
