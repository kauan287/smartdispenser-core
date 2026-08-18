using MediatR;
using Microsoft.Extensions.Logging;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Entities;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.Core.Commands.ProcessPaymentWebhook;

// ── Command ───────────────────────────────────────────────────────────────────

public sealed record ProcessPaymentWebhookCommand(PaymentWebhookPayload Payload) : IRequest<ProcessPaymentResult>;

public sealed record ProcessPaymentResult
{
    public bool Success { get; init; }
    public bool WasDuplicate { get; init; }
    public string? ErrorMessage { get; init; }

    public static ProcessPaymentResult Ok() => new() { Success = true };
    public static ProcessPaymentResult Duplicate() => new() { Success = true, WasDuplicate = true };
    public static ProcessPaymentResult Fail(string err) => new() { Success = false, ErrorMessage = err };
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Processes an incoming payment webhook with idempotency guarantees.
///
/// Idempotency strategy:
///   Check idempotency key in DB before processing.
///   If duplicate → return 200 immediately (webhook provider expects this).
///   If new → process and save in same transaction.
///
/// After confirming payment, sends a MediatR notification to trigger
/// the dispense command (via InitiateDispenseHandler).
/// </summary>
public sealed class ProcessPaymentWebhookHandler
    : IRequestHandler<ProcessPaymentWebhookCommand, ProcessPaymentResult>
{
    private readonly IPaymentRepository _paymentRepo;
    private readonly IDispenseRequestRepository _requestRepo;
    private readonly IMediator _mediator;
    private readonly ILogger<ProcessPaymentWebhookHandler> _logger;

    public ProcessPaymentWebhookHandler(
        IPaymentRepository paymentRepo,
        IDispenseRequestRepository requestRepo,
        IMediator mediator,
        ILogger<ProcessPaymentWebhookHandler> logger)
    {
        _paymentRepo = paymentRepo;
        _requestRepo = requestRepo;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task<ProcessPaymentResult> Handle(
        ProcessPaymentWebhookCommand cmd,
        CancellationToken ct)
    {
        var payload = cmd.Payload;

        _logger.LogInformation(
            "Processing payment webhook: TxId={TxId} IdempotencyKey={Key}",
            payload.TransactionId, payload.IdempotencyKey);

        // ── Idempotency check ─────────────────────────────────────────────────
        var existing = await _paymentRepo.GetByIdempotencyKeyAsync(payload.IdempotencyKey, ct);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Duplicate webhook detected (IdempotencyKey={Key}). Skipping.", payload.IdempotencyKey);
            return ProcessPaymentResult.Duplicate();
        }

        // ── Resolve DispenseRequest ───────────────────────────────────────────
        if (!Guid.TryParse(payload.DispenseRequestId, out var requestId))
            return ProcessPaymentResult.Fail("Invalid DispenseRequestId format.");

        var dispenseRequest = await _requestRepo.GetByIdAsync(requestId, ct);
        if (dispenseRequest is null)
            return ProcessPaymentResult.Fail($"DispenseRequest {requestId} not found.");

        // ── Create Payment record ─────────────────────────────────────────────
        var payment = Payment.CreateFromWebhook(
            dispenseRequestId: dispenseRequest.Id,
            externalTransactionId: payload.TransactionId,
            idempotencyKey: payload.IdempotencyKey,
            amount: payload.Amount,
            currency: payload.Currency,
            gatewayProvider: payload.Provider);

        // ── Apply gateway result ──────────────────────────────────────────────
        if (payload.Status.Equals("confirmed", StringComparison.OrdinalIgnoreCase))
        {
            payment.Confirm(payload.GatewayResponse);
            dispenseRequest.MarkPaymentReceived(payment.Id);

            await _paymentRepo.AddAsync(payment, ct);
            await _requestRepo.UpdateAsync(dispenseRequest, ct);

            // Trigger dispense via MediatR (next step in the pipeline)
            var dispenseResult = await _mediator.Send(
                new InitiateDispense.InitiateDispenseCommand(
                    DispenserId: dispenseRequest.DispenserId,
                    PrescriptionId: dispenseRequest.PrescriptionId,
                    PatientIdentifier: dispenseRequest.PatientIdentifier),
                ct);

            if (!dispenseResult.Success)
            {
                _logger.LogWarning(
                    "Payment confirmed but dispense failed: {Reason}", dispenseResult.ErrorMessage);
                // Payment is still recorded as confirmed — business decision:
                // refund flow should be triggered separately
            }
        }
        else
        {
            payment.Fail(payload.GatewayResponse ?? "Gateway returned failure status.");
            dispenseRequest.MarkFailed($"Payment failed: {payload.GatewayResponse}");

            await _paymentRepo.AddAsync(payment, ct);
            await _requestRepo.UpdateAsync(dispenseRequest, ct);

            _logger.LogWarning(
                "Payment failed for DispenseRequest {RequestId}: {Reason}",
                requestId, payload.GatewayResponse);
        }

        return ProcessPaymentResult.Ok();
    }
}
