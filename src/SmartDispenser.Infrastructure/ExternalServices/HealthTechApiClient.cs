using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.Infrastructure.ExternalServices;

/// <summary>
/// HTTP client for the HealthTech Agent Orchestrator API (Project 1).
///
/// Resilience strategy (configured via Microsoft.Extensions.Http.Resilience):
///   - Retry: 3 attempts, exponential backoff (2s, 4s, 8s)
///   - Circuit Breaker: opens after 5 failures in 30s, half-open after 60s
///   - Timeout: 10s per individual request
///
/// The circuit breaker prevents the dispenser system from hanging on
/// every request when the HealthTech API is unavailable.
/// </summary>
public sealed class HealthTechApiClient : IHealthTechApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HealthTechApiClient> _logger;

    public HealthTechApiClient(HttpClient httpClient, ILogger<HealthTechApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Fetches prescription state from the HealthTech Orchestrator.
    /// Maps from the Python API response shape into our domain DTO.
    /// </summary>
    public async Task<PrescriptionVerificationResult> VerifyPrescriptionAsync(
        string prescriptionId,
        CancellationToken ct = default)
    {
        // The HealthTech API uses thread_id = "session-{prescription_id}"
        var threadId = $"session-{prescriptionId}";

        _logger.LogDebug(
            "Verifying prescription {PrescriptionId} (thread_id={ThreadId})",
            prescriptionId, threadId);

        var response = await _httpClient.GetAsync(
            $"/api/v1/prescriptions/{threadId}", ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Prescription {Id} not found in HealthTech API", prescriptionId);
            throw new PrescriptionNotFoundException(prescriptionId);
        }

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<HealthTechPrescriptionResponse>(
            cancellationToken: ct);

        if (payload is null)
            throw new InvalidOperationException("HealthTech API returned empty response.");

        _logger.LogInformation(
            "Prescription {Id}: approved={Approved}, safetyOk={Safety}, auditOk={Audit}",
            prescriptionId, payload.human_approved, payload.safety_approved, payload.audit_chain_valid);

        return new PrescriptionVerificationResult
        {
            PrescriptionId = payload.prescription_id,
            ThreadId = threadId,
            IsApproved = payload.human_approved == true,
            AuditChainValid = payload.audit_chain_valid,
            SafetyApproved = payload.safety_approved,
            IsPendingApproval = payload.is_paused_waiting_approval,
            ProposedMedication = payload.proposed_medication,
            Dosage = payload.dosage,
            RiskNotes = payload.risk_notes,
            DoctorComments = payload.doctor_comments,
        };
    }

    // ── Private DTO — matches the Python FastAPI response shape ───────────────

    private sealed class HealthTechPrescriptionResponse
    {
        public string thread_id { get; set; } = string.Empty;
        public string prescription_id { get; set; } = string.Empty;
        public string? proposed_medication { get; set; }
        public string? dosage { get; set; }
        public bool safety_approved { get; set; }
        public string? risk_notes { get; set; }
        public bool? human_approved { get; set; }
        public string? doctor_comments { get; set; }
        public bool is_paused_waiting_approval { get; set; }
        public bool audit_chain_valid { get; set; }
    }
}

/// <summary>Thrown when the HealthTech API returns 404 for a prescription.</summary>
public sealed class PrescriptionNotFoundException(string prescriptionId)
    : Exception($"Prescription '{prescriptionId}' was not found in the HealthTech Orchestrator.");
