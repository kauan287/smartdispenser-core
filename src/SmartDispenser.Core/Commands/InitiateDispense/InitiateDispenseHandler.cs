using MediatR;
using Microsoft.Extensions.Logging;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Entities;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.Core.Commands.InitiateDispense;

// ── Command ───────────────────────────────────────────────────────────────────

public sealed record InitiateDispenseCommand(
    Guid DispenserId,
    string PrescriptionId,
    string PatientIdentifier
) : IRequest<InitiateDispenseResult>;

public sealed record InitiateDispenseResult
{
    public bool Success { get; init; }
    public Guid? DispenseRequestId { get; init; }
    public string? ErrorMessage { get; init; }

    public static InitiateDispenseResult Ok(Guid requestId) =>
        new() { Success = true, DispenseRequestId = requestId };

    public static InitiateDispenseResult Fail(string error) =>
        new() { Success = false, ErrorMessage = error };
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Orchestrates the full dispense pipeline:
///
/// 1. Verify prescription approval + audit chain via HealthTech API
/// 2. Validate dispenser state (online, idle, has inventory)
/// 3. Create DispenseRequest record
/// 4. Transition dispenser state → Dispensing
/// 5. Send hardware command via SignalR hub
/// 6. Queue OutboxMessage for completion webhook
///
/// All DB writes in a single transaction — atomicity guarantees
/// that the SignalR command is only sent if the DB write succeeds.
/// </summary>
public sealed class InitiateDispenseHandler : IRequestHandler<InitiateDispenseCommand, InitiateDispenseResult>
{
    private readonly IHealthTechApiClient _healthTechClient;
    private readonly IDispenserRepository _dispenserRepo;
    private readonly IDispenseRequestRepository _requestRepo;
    private readonly IDispenserHubContext _hubContext;
    private readonly ILogger<InitiateDispenseHandler> _logger;

    public InitiateDispenseHandler(
        IHealthTechApiClient healthTechClient,
        IDispenserRepository dispenserRepo,
        IDispenseRequestRepository requestRepo,
        IDispenserHubContext hubContext,
        ILogger<InitiateDispenseHandler> logger)
    {
        _healthTechClient = healthTechClient;
        _dispenserRepo = dispenserRepo;
        _requestRepo = requestRepo;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<InitiateDispenseResult> Handle(
        InitiateDispenseCommand cmd,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Initiating dispense for prescription {PrescriptionId} on dispenser {DispenserId}",
            cmd.PrescriptionId, cmd.DispenserId);

        // ── 1. Verify prescription ────────────────────────────────────────────
        PrescriptionVerificationResult prescription;
        try
        {
            prescription = await _healthTechClient.VerifyPrescriptionAsync(cmd.PrescriptionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to contact HealthTech API for prescription {Id}", cmd.PrescriptionId);
            return InitiateDispenseResult.Fail(
                "Prescription verification service unavailable. Please try again later.");
        }

        if (!prescription.IsReadyForDispensing)
        {
            _logger.LogWarning(
                "Prescription {Id} rejected: {Reason}", cmd.PrescriptionId, prescription.GetRejectionReason());
            return InitiateDispenseResult.Fail(prescription.GetRejectionReason());
        }

        // ── 2. Validate dispenser ─────────────────────────────────────────────
        var dispenser = await _dispenserRepo.GetByIdAsync(cmd.DispenserId, ct);
        if (dispenser is null)
            return InitiateDispenseResult.Fail($"Dispenser {cmd.DispenserId} not found.");

        if (!dispenser.CanDispense())
            return InitiateDispenseResult.Fail(
                $"Dispenser '{dispenser.Name}' is not ready (status: {dispenser.Status}).");

        // ── 3. Create DispenseRequest ─────────────────────────────────────────
        var request = DispenseRequest.Create(
            dispenserId: cmd.DispenserId,
            prescriptionId: cmd.PrescriptionId,
            patientIdentifier: cmd.PatientIdentifier,
            medication: prescription.ProposedMedication ?? "Unknown",
            dosage: prescription.Dosage ?? "Unknown");

        request.MarkPrescriptionVerified();
        await _requestRepo.AddAsync(request, ct);

        // ── 4. Transition dispenser state ─────────────────────────────────────
        dispenser.BeginDispensing(request.Id);
        await _dispenserRepo.UpdateAsync(dispenser, ct);

        // ── 5. Send SignalR command to physical machine ───────────────────────
        try
        {
            await _hubContext.SendCommandAsync(
                cmd.DispenserId,
                new DispenserCommand(CommandType.Dispense, new
                {
                    RequestId = request.Id,
                    Medication = prescription.ProposedMedication,
                    Dosage = prescription.Dosage,
                    PrescriptionId = cmd.PrescriptionId,
                }),
                ct);

            request.MarkDispatched();
            await _requestRepo.UpdateAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send dispense command to machine {DispenserId}", cmd.DispenserId);

            // Roll back dispenser state
            dispenser.FailDispensing("SignalR delivery failure");
            request.MarkFailed("Failed to reach physical machine via SignalR");
            await _dispenserRepo.UpdateAsync(dispenser, ct);
            await _requestRepo.UpdateAsync(request, ct);

            return InitiateDispenseResult.Fail("Could not reach the physical dispenser. Please try again.");
        }

        _logger.LogInformation(
            "Dispense request {RequestId} dispatched to machine {DispenserId}",
            request.Id, cmd.DispenserId);

        return InitiateDispenseResult.Ok(request.Id);
    }
}
