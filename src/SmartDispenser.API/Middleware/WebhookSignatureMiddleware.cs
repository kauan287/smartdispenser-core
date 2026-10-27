using System.Security.Cryptography;
using System.Text;

namespace SmartDispenser.API.Middleware;

/// <summary>
/// Rejects payment webhooks whose <c>X-Signature-SHA256: sha256={hex}</c> header is not the
/// HMAC-SHA256 of the raw body under <c>Webhook:Secret</c>. Runs before model binding so a
/// forged payload never reaches a controller.
/// </summary>
public sealed class WebhookSignatureMiddleware
{
    private const string WebhookPath = "/api/payments/webhook";
    private const string SignatureHeader = "X-Signature-SHA256";
    private const int MinSecretLength = 32;
    private const int MaxBodyBytes = 512 * 1024;

    private readonly RequestDelegate _next;
    private readonly ILogger<WebhookSignatureMiddleware> _logger;
    private readonly byte[] _key;

    public WebhookSignatureMiddleware(
        RequestDelegate next,
        ILogger<WebhookSignatureMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;

        var secret = configuration["Webhook:Secret"] ?? "";
        if (secret.Length < MinSecretLength)
            throw new InvalidOperationException($"Webhook:Secret must be at least {MinSecretLength} characters.");
        _key = Encoding.UTF8.GetBytes(secret);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(WebhookPath))
        {
            await _next(context);
            return;
        }

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        context.Request.EnableBuffering();
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        context.Request.Body.Position = 0;

        var received = context.Request.Headers[SignatureHeader].ToString();
        if (received.Length == 0)
        {
            await Reject(context, "Missing signature header.");
            return;
        }

        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(_key, buffer.ToArray()));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received), Encoding.UTF8.GetBytes(expected)))
        {
            await Reject(context, "Invalid signature.");
            return;
        }

        await _next(context);
    }

    private async Task Reject(HttpContext context, string reason)
    {
        _logger.LogWarning("Webhook rejected: {Reason}", reason);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync(reason);
    }
}

public static class WebhookSignatureMiddlewareExtensions
{
    public static IApplicationBuilder UseWebhookSignatureValidation(this IApplicationBuilder app) =>
        app.UseMiddleware<WebhookSignatureMiddleware>();
}
