using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.Infrastructure.ExternalServices;

/// <summary>
/// Reads prescriptions from the HealthTech Agent Orchestrator at
/// <c>GET /api/v1/prescriptions/session-{prescription_id}</c>. Retries, timeouts and the
/// circuit breaker come from the resilience handler registered in DependencyInjection.
/// </summary>
public sealed class HealthTechApiClient(HttpClient httpClient, ILogger<HealthTechApiClient> logger) : IHealthTechApiClient
{
    public async Task<PrescriptionVerificationResult?> VerifyPrescriptionAsync(string prescriptionId, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/v1/prescriptions/session-{Uri.EscapeDataString(prescriptionId)}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        var session = await response.Content.ReadFromJsonAsync<OrchestratorSession>(ct)
            ?? throw new InvalidOperationException("HealthTech API returned an empty body.");

        logger.LogInformation(
            "Prescription {PrescriptionId}: human_approved={Approved} safety_approved={Safety} audit_chain_valid={Audit}",
            prescriptionId, session.HumanApproved, session.SafetyApproved, session.AuditChainValid);

        return new PrescriptionVerificationResult
        {
            PrescriptionId = session.PrescriptionId,
            ThreadId = session.ThreadId,
            IsApproved = session.HumanApproved == true,
            SafetyApproved = session.SafetyApproved,
            AuditChainValid = session.AuditChainValid,
            IsPendingApproval = session.IsPausedWaitingApproval,
            ProposedMedication = session.ProposedMedication,
            Dosage = session.Dosage,
            RiskNotes = session.RiskNotes,
            DoctorComments = session.DoctorComments,
        };
    }

    private sealed record OrchestratorSession(
        [property: JsonPropertyName("thread_id")] string ThreadId,
        [property: JsonPropertyName("prescription_id")] string PrescriptionId,
        [property: JsonPropertyName("proposed_medication")] string? ProposedMedication,
        [property: JsonPropertyName("dosage")] string? Dosage,
        [property: JsonPropertyName("safety_approved")] bool SafetyApproved,
        [property: JsonPropertyName("risk_notes")] string? RiskNotes,
        [property: JsonPropertyName("human_approved")] bool? HumanApproved,
        [property: JsonPropertyName("doctor_comments")] string? DoctorComments,
        [property: JsonPropertyName("is_paused_waiting_approval")] bool IsPausedWaitingApproval,
        [property: JsonPropertyName("audit_chain_valid")] bool AuditChainValid);
}
