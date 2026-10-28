using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using SmartDispenser.API.BackgroundServices;
using SmartDispenser.API.Hubs;
using SmartDispenser.API.Middleware;
using SmartDispenser.API.RateLimiting;
using SmartDispenser.API.SignalR;
using SmartDispenser.Core.Commands.InitiateDispense;
using SmartDispenser.Core.Interfaces;
using SmartDispenser.Infrastructure;
using SmartDispenser.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, services, config) => config
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

builder.Services.AddControllers();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info = new()
    {
        Title = "SmartDispenser Core API",
        Version = "v1",
        Description = "Backend for smart pharmaceutical vending machines. Verifies prescriptions with the " +
                      "HealthTech Agent Orchestrator before a machine is allowed to dispense.",
    };
    return Task.CompletedTask;
}));

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<InitiateDispenseHandler>());
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddSignalR(opts =>
{
    opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
    opts.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    opts.KeepAliveInterval = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<DispenserNotifier>();
builder.Services.AddSingleton<IDispenserNotifier>(sp => sp.GetRequiredService<DispenserNotifier>());

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddRateLimiter(opts =>
{
    opts.AddSlidingWindowLimiter("webhook", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromSeconds(1);
        limiter.SegmentsPerWindow = 2;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiter.QueueLimit = 5;
    });
    opts.AddClientRateLimit(builder.Configuration);
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opts.OnRejected = (ctx, _) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            ctx.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    };
});

builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.AddHostedService<HeartbeatMonitor>();

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.UseSerilogRequestLogging(opts => opts.GetLevel = (ctx, _, _) =>
    ctx.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Debug : LogEventLevel.Information);

app.MapOpenApi();
app.UseSwaggerUI(opts => opts.SwaggerEndpoint("/openapi/v1.json", "SmartDispenser Core API v1"));

app.UseRateLimiter();
app.UseWebhookSignatureValidation();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapControllers();
app.MapHub<DispenserHub>("/hubs/dispensers");
app.MapRazorComponents<SmartDispenser.API.Components.App>().AddInteractiveServerRenderMode();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();
