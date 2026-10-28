using MediatR;
using Microsoft.Extensions.Logging;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Entities;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.Core.Commands.InitiateDispense;

public sealed record InitiateDispenseCommand(
    Guid DispenserId,
    string PrescriptionId,
    string PatientIdentifier
) : IRequest<InitiateDispenseResult>;

public enum InitiateDispenseError
{
    DispenserNotFound,
    Rejected,
    VerificationUnavailable,
}

public sealed record InitiateDispenseResult
{
    public Guid? DispenseRequestId { get; private init; }
    public InitiateDispenseError? Error { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static InitiateDispenseResult Ok(Guid requestId) => new() { DispenseRequestId = requestId };

    public static InitiateDispenseResult Fail(InitiateDispenseError error, string message) =>
        new() { Error = error, ErrorMessage = message };
}

public sealed class InitiateDispenseHandler(
    IHealthTechApiClient healthTechClient,
    IDispenserRepository dispenserRepo,
    IDispenseRequestRepository requestRepo,
    IUnitOfWork unitOfWork,
    ILogger<InitiateDispenseHandler> logger)
    : IRequestHandler<InitiateDispenseCommand, InitiateDispenseResult>
{
    public async Task<InitiateDispenseResult> Handle(InitiateDispenseCommand cmd, CancellationToken ct)
    {
        var dispenser = await dispenserRepo.GetByIdAsync(cmd.DispenserId, ct);
        if (dispenser is null)
            return InitiateDispenseResult.Fail(InitiateDispenseError.DispenserNotFound, $"Dispenser {cmd.DispenserId} not found.");

        PrescriptionVerificationResult? prescription;
        try
        {
            prescription = await healthTechClient.VerifyPrescriptionAsync(cmd.PrescriptionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "HealthTech API call failed for prescription {PrescriptionId}", cmd.PrescriptionId);
            return InitiateDispenseResult.Fail(InitiateDispenseError.VerificationUnavailable,
                "Prescription verification service unavailable. Please try again later.");
        }

        if (prescription is null)
            return Reject($"Prescription '{cmd.PrescriptionId}' was not found in the HealthTech Orchestrator.");
        if (prescription.RejectionReason is { } reason)
            return Reject(reason);
        if (prescription is not { ProposedMedication: { } medication, Dosage: { } dosage })
            return Reject("Prescription has no medication or dosage.");
        if (dosage.Length > DispenseRequest.DosageMaxLength)
            return Reject($"Prescription dosage is longer than {DispenseRequest.DosageMaxLength} characters.");
        if (!dispenser.Stocks(medication))
            return Reject($"This dispenser stocks {dispenser.Medication}, but the prescription is for {medication}.");
        if (!dispenser.CanDispense())
            return Reject($"Dispenser '{dispenser.Name}' is not ready (status: {dispenser.Status}).");
        var inUse = $"Prescription '{cmd.PrescriptionId}' already has a dispense request.";
        if (await requestRepo.IsPrescriptionInUseAsync(cmd.PrescriptionId, ct))
            return Reject(inUse);

        var request = DispenseRequest.Create(cmd.DispenserId, cmd.PrescriptionId, cmd.PatientIdentifier, medication, dosage);
        requestRepo.Add(request);
        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (ConflictException)
        {
            return Reject(inUse);
        }

        logger.LogInformation("Dispense request {RequestId} awaiting payment", request.Id);
        return InitiateDispenseResult.Ok(request.Id);

        InitiateDispenseResult Reject(string message)
        {
            logger.LogWarning("Dispense of prescription {PrescriptionId} rejected: {Reason}", cmd.PrescriptionId, message);
            return InitiateDispenseResult.Fail(InitiateDispenseError.Rejected, message);
        }
    }
}
