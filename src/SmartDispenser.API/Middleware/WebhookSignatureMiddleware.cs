using System.Security.Cryptography;
using System.Text;

namespace SmartDispenser.API.Middleware;

/// <summary>
/// Validates HMAC-SHA256 webhook signatures before the request reaches any controller.
///
/// Security properties:
///   1. Raw body is buffered (EnableBuffering) so it can be re-read by controllers.
///   2. FixedTimeEquals() prevents timing attacks (no early exit on mismatch).
///   3. Invalid signatures return 401, not 400 — avoids leaking whether the endpoint exists.
///   4. The raw body is stored in HttpContext.Items for downstream deserialization.
///
/// Expected header: X-Signature-SHA256: sha256={hex_hmac}
/// This mirrors the Stripe and GitHub webhook validation convention.
/// </summary>
public sealed class WebhookSignatureMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<WebhookSignatureMiddleware> _logger;
    private readonly string _webhookSecret;

    private const string SignatureHeader = "X-Signature-SHA256";
    private const string RawBodyKey = "RawBody";
    private const int MaxBodyBytes = 1024 * 512; // 512 KB

    public WebhookSignatureMiddleware(
        RequestDelegate next,
        ILogger<WebhookSignatureMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _webhookSecret = configuration["Webhook:Secret"]
            ?? throw new InvalidOperationException("Webhook:Secret is not configured.");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only validate requests to the payment webhook endpoint
        if (!context.Request.Path.StartsWithSegments("/api/payments/webhook"))
        {
            await _next(context);
            return;
        }

        // Buffer the body so it can be re-read by the controller
        context.Request.EnableBuffering();

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            _logger.LogWarning("Webhook body exceeds maximum allowed size ({Max} bytes)", MaxBodyBytes);
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        // Read the raw body
        using var reader = new StreamReader(
            context.Request.Body,
            encoding: Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);

        var rawBody = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0; // Reset for controller

        // Store raw body for downstream use (signature logging, idempotency)
        context.Items[RawBodyKey] = rawBody;

        // Validate signature header
        if (!context.Request.Headers.TryGetValue(SignatureHeader, out var receivedHeader)
            || string.IsNullOrEmpty(receivedHeader))
        {
            _logger.LogWarning("Webhook request missing {Header} header", SignatureHeader);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing signature header.");
            return;
        }

        var receivedSignature = receivedHeader.ToString();

        // Compute expected HMAC-SHA256
        var expectedSignature = ComputeHmacSha256(rawBody, _webhookSecret);

        // Use FixedTimeEquals to prevent timing attacks
        var receivedBytes = Encoding.UTF8.GetBytes(receivedSignature);
        var expectedBytes = Encoding.UTF8.GetBytes($"sha256={expectedSignature}");

        if (!CryptographicOperations.FixedTimeEquals(receivedBytes, expectedBytes))
        {
            _logger.LogWarning(
                "Webhook signature mismatch. Received: {Received}, Expected prefix: sha256=...",
                receivedSignature[..Math.Min(20, receivedSignature.Length)]);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid signature.");
            return;
        }

        _logger.LogDebug("Webhook signature validated successfully");
        await _next(context);
    }

    private static string ComputeHmacSha256(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(payloadBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}

/// <summary>Extension method for clean registration in Program.cs.</summary>
public static class WebhookSignatureMiddlewareExtensions
{
    public static IApplicationBuilder UseWebhookSignatureValidation(this IApplicationBuilder app) =>
        app.UseMiddleware<WebhookSignatureMiddleware>();
}
