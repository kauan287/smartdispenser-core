using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace SmartDispenser.API.RateLimiting;

/// <summary>
/// Opt-in fixed-window limit of <c>RateLimit:PermitsPerMinute</c> requests per client, keyed by
/// <c>RateLimit:ClientIpHeader</c> when set (e.g. <c>CF-Connecting-IP</c> behind Cloudflare),
/// otherwise by the connection's remote address. Health probes are never limited.
/// </summary>
public static class ClientRateLimit
{
    public static void AddClientRateLimit(this RateLimiterOptions options, IConfiguration configuration)
    {
        var permitsPerMinute = configuration.GetValue<int>("RateLimit:PermitsPerMinute");
        if (permitsPerMinute < 0)
            throw new InvalidOperationException("RateLimit:PermitsPerMinute must not be negative.");
        if (permitsPerMinute == 0)
            return;

        var clientIpHeader = configuration["RateLimit:ClientIpHeader"] ?? "";
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            context.Request.Path.StartsWithSegments("/health")
                ? RateLimitPartition.GetNoLimiter("health")
                : RateLimitPartition.GetFixedWindowLimiter(ClientKey(context, clientIpHeader), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
    }

    private static string ClientKey(HttpContext context, string clientIpHeader)
    {
        if (clientIpHeader.Length > 0 && context.Request.Headers[clientIpHeader].FirstOrDefault() is { Length: > 0 } ip)
            return ip;
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
