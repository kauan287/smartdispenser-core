using MediatR;
using Microsoft.AspNetCore.Mvc;
using SmartDispenser.Core.Commands.ProcessPaymentWebhook;
using SmartDispenser.Core.DTOs;

namespace SmartDispenser.API.Controllers;

/// <summary>
/// Payment gateway webhook endpoint.
///
/// Security: HMAC-SHA256 signature is validated BEFORE this controller
/// is reached (via WebhookSignatureMiddleware).
///
/// Rate limiting: applied via the "webhook" policy (5 req/s per IP).
///
/// Design: returns 200 immediately to prevent gateway retry storms.
/// Actual processing is async via MediatR. Idempotency is handled
/// by the ProcessPaymentWebhookHandler.
/// </summary>
[ApiController]
[Route("api/payments")]
[Produces("application/json")]
public sealed class PaymentsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(IMediator mediator, ILogger<PaymentsController> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    /// <summary>
    /// Receives a payment gateway webhook event.
    /// The signature has already been validated by WebhookSignatureMiddleware.
    /// </summary>
    [HttpPost("webhook")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]   // Signature failure (middleware)
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)] // Rate limit
    public async Task<IActionResult> HandleWebhook(
        [FromBody] PaymentWebhookPayload payload,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Webhook received: TxId={TxId} Provider={Provider} Status={Status}",
            payload.TransactionId, payload.Provider, payload.Status);

        var result = await _mediator.Send(new ProcessPaymentWebhookCommand(payload), ct);

        if (result.WasDuplicate)
        {
            _logger.LogInformation("Duplicate webhook ignored (IdempotencyKey={Key})", payload.IdempotencyKey);
        }
        else if (!result.Success)
        {
            // Log the failure but still return 200 — prevents gateway from retrying forever.
            // The OutboxMessage / Dead Letter mechanism handles permanent failures.
            _logger.LogError("Webhook processing failed: {Error}", result.ErrorMessage);
        }

        // Always return 200 to the gateway provider
        return Ok(new { received = true, duplicate = result.WasDuplicate });
    }

    /// <summary>List recent transactions (for dashboard).</summary>
    [HttpGet("recent")]
    [ProducesResponseType(typeof(IReadOnlyList<PaymentSummary>), StatusCodes.Status200OK)]
    public IActionResult GetRecent() =>
        Ok(Array.Empty<PaymentSummary>()); // TODO: wire up repository query

    public sealed record PaymentSummary(
        Guid Id,
        string ExternalTransactionId,
        decimal Amount,
        string Currency,
        string Status,
        string Provider,
        DateTimeOffset CreatedAt);
}
