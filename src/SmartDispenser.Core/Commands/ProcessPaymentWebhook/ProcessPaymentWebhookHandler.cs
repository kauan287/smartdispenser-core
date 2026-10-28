using MediatR;
using Microsoft.Extensions.Logging;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Entities;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.Core.Commands.ProcessPaymentWebhook;

public sealed record ProcessPaymentWebhookCommand(PaymentWebhookPayload Payload) : IRequest<ProcessPaymentResult>;

public sealed record ProcessPaymentResult
{
    public bool Success { get; private init; }
    public bool WasDuplicate { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static ProcessPaymentResult Ok() => new() { Success = true };
    public static ProcessPaymentResult Duplicate() => new() { Success = true, WasDuplicate = true };
    public static ProcessPaymentResult Fail(string err) => new() { ErrorMessage = err };
}

public sealed class ProcessPaymentWebhookHandler(
    IPaymentRepository paymentRepo,
    IDispenseRequestRepository requestRepo,
    IDispenserRepository dispenserRepo,
    IDispenserNotifier notifier,
    IUnitOfWork unitOfWork,
    ILogger<ProcessPaymentWebhookHandler> logger)
    : IRequestHandler<ProcessPaymentWebhookCommand, ProcessPaymentResult>
{
    public async Task<ProcessPaymentResult> Handle(ProcessPaymentWebhookCommand cmd, CancellationToken ct)
    {
        var payload = cmd.Payload;

        if (await paymentRepo.GetByIdempotencyKeyAsync(payload.IdempotencyKey, ct) is not null)
            return ProcessPaymentResult.Duplicate();

        if (!Guid.TryParse(payload.DispenseRequestId, out var requestId))
            return ProcessPaymentResult.Fail("Invalid DispenseRequestId format.");

        var request = await requestRepo.GetByIdAsync(requestId, ct);
        if (request is null)
            return ProcessPaymentResult.Fail($"DispenseRequest {requestId} not found.");
        if (request.Status != DispenseRequestStatus.PendingPayment)
            return ProcessPaymentResult.Fail($"DispenseRequest {requestId} is not awaiting payment (status: {request.Status}).");

        var outcome = Classify(payload.Status);
        if (outcome == GatewayOutcome.Ignored)
        {
            logger.LogInformation("Ignored gateway status {Status} for dispense request {RequestId}", payload.Status, request.Id);
            return ProcessPaymentResult.Ok();
        }

        var payment = Payment.CreateFromWebhook(
            request.Id, payload.TransactionId, payload.IdempotencyKey,
            payload.Amount, payload.Currency, payload.Provider);
        paymentRepo.Add(payment);

        if (outcome == GatewayOutcome.Failed)
        {
            payment.Fail(payload.GatewayResponse ?? "Gateway returned failure status.");
            request.MarkFailed($"Payment failed: {payment.GatewayResponse}");
            await unitOfWork.SaveChangesAsync(ct);
            return ProcessPaymentResult.Ok();
        }

        payment.Confirm(payload.GatewayResponse);
        request.RecordPayment(payment.Id);

        var dispenser = await dispenserRepo.GetByIdAsync(request.DispenserId, ct);
        if (dispenser is null || !dispenser.CanDispense())
        {
            request.MarkFailed(
                $"Dispenser was not ready when payment {payment.Id} arrived (status: {dispenser?.Status}). Refund required.");
            await unitOfWork.SaveChangesAsync(ct);
            logger.LogWarning("Paid dispense request {RequestId} could not be dispatched", request.Id);
            return ProcessPaymentResult.Ok();
        }

        dispenser.BeginDispensing(request.Id);
        request.MarkDispatched();
        await unitOfWork.SaveChangesAsync(ct);

        await notifier.SendDispenseCommandAsync(
            dispenser.Id,
            new DispenseCommand(request.Id, request.PrescriptionId, request.Medication, request.Dosage),
            ct);
        await notifier.PublishStateAsync(DispenserStateDto.From(dispenser), ct);
        logger.LogInformation("Dispense request {RequestId} dispatched to machine {DispenserId}", request.Id, dispenser.Id);
        return ProcessPaymentResult.Ok();
    }

    private enum GatewayOutcome { Confirmed, Failed, Ignored }

    private static GatewayOutcome Classify(string status) => status.ToLowerInvariant() switch
    {
        "confirmed" => GatewayOutcome.Confirmed,
        "failed" or "declined" or "cancelled" or "expired" => GatewayOutcome.Failed,
        _ => GatewayOutcome.Ignored,
    };
}
