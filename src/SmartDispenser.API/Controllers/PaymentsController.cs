using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartDispenser.Core.Commands.ProcessPaymentWebhook;
using SmartDispenser.Core.DTOs;

namespace SmartDispenser.API.Controllers;

[ApiController]
[Route("api/payments")]
[Produces("application/json")]
public sealed class PaymentsController(IMediator mediator, ILogger<PaymentsController> logger) : ControllerBase
{
    /// <summary>
    /// Payment gateway webhook. WebhookSignatureMiddleware has already checked the
    /// X-Signature-SHA256 header. Every processed event answers 200 so the gateway does not
    /// retry a payload that will never succeed; a repeated idempotency key is a no-op.
    /// </summary>
    [HttpPost("webhook")]
    [EnableRateLimiting("webhook")]
    [ProducesResponseType<WebhookAck>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> HandleWebhook([FromBody] PaymentWebhookPayload payload, CancellationToken ct)
    {
        var result = await mediator.Send(new ProcessPaymentWebhookCommand(payload), ct);
        if (!result.Success)
            logger.LogError("Webhook {TransactionId} not applied: {Error}", payload.TransactionId, result.ErrorMessage);

        return Ok(new WebhookAck(Received: true, Duplicate: result.WasDuplicate));
    }

    public sealed record WebhookAck(bool Received, bool Duplicate);
}
