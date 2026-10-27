using System.Net;
using SmartDispenser.Infrastructure.ExternalServices;

namespace SmartDispenser.UnitTests.Contracts;

/// <summary>
/// Pins the response shape of the HealthTech Agent Orchestrator's
/// GET /api/v1/prescriptions/session-{prescription_id} (FastAPI, snake_case).
/// </summary>
public sealed class HealthTechApiContractTests
{
    private const string OrchestratorResponse = """
        {
          "thread_id": "session-RX-2024-001",
          "prescription_id": "RX-2024-001",
          "proposed_medication": "Ibuprofen",
          "dosage": "400 mg every 8 hours",
          "safety_approved": true,
          "risk_notes": "No known allergies",
          "human_approved": true,
          "doctor_comments": "OK to dispense",
          "is_paused_waiting_approval": false,
          "audit_chain_valid": true,
          "field_added_later": "ignored"
        }
        """;

    [Fact]
    public async Task Maps_the_orchestrator_response_into_a_verification_result()
    {
        var (client, handler) = Client(HttpStatusCode.OK, OrchestratorResponse);

        var result = await client.VerifyPrescriptionAsync("RX-2024-001");

        handler.RequestedPath.Should().Be("/api/v1/prescriptions/session-RX-2024-001");
        result.Should().BeEquivalentTo(new PrescriptionVerificationResult
        {
            PrescriptionId = "RX-2024-001",
            ThreadId = "session-RX-2024-001",
            IsApproved = true,
            SafetyApproved = true,
            AuditChainValid = true,
            IsPendingApproval = false,
            ProposedMedication = "Ibuprofen",
            Dosage = "400 mg every 8 hours",
            RiskNotes = "No known allergies",
            DoctorComments = "OK to dispense",
        });
        result!.RejectionReason.Should().BeNull();
    }

    [Fact]
    public async Task Paused_session_with_null_human_decision_maps_to_pending()
    {
        var paused = """
            {"thread_id":"session-RX-7","prescription_id":"RX-7","proposed_medication":"Ibuprofen","dosage":"400 mg",
             "safety_approved":true,"risk_notes":null,"human_approved":null,"doctor_comments":null,
             "is_paused_waiting_approval":true,"audit_chain_valid":true}
            """;
        var (client, _) = Client(HttpStatusCode.OK, paused);

        var result = await client.VerifyPrescriptionAsync("RX-7");

        result!.IsApproved.Should().BeFalse();
        result.IsPendingApproval.Should().BeTrue();
        result.RejectionReason.Should().Be("Prescription is pending doctor approval.");
    }

    [Fact]
    public async Task Tampered_audit_chain_is_reported_before_any_other_gate()
    {
        var invalid = """
            {"thread_id":"session-RX-9","prescription_id":"RX-9","proposed_medication":null,"dosage":null,
             "safety_approved":false,"risk_notes":null,"human_approved":null,"doctor_comments":null,
             "is_paused_waiting_approval":false,"audit_chain_valid":false,"status":"INVALID"}
            """;
        var (client, _) = Client(HttpStatusCode.OK, invalid);

        var result = await client.VerifyPrescriptionAsync("RX-9");

        result!.AuditChainValid.Should().BeFalse();
        result.RejectionReason.Should().Be("Audit chain integrity check failed: possible data tampering.");
    }

    [Fact]
    public async Task Missing_approval_field_fails_closed()
    {
        var withoutAuditFlag = """
            {"thread_id":"session-RX-8","prescription_id":"RX-8","proposed_medication":"Ibuprofen","dosage":"400 mg",
             "safety_approved":true,"risk_notes":null,"human_approved":true,"doctor_comments":null,
             "is_paused_waiting_approval":false}
            """;
        var (client, _) = Client(HttpStatusCode.OK, withoutAuditFlag);

        var result = await client.VerifyPrescriptionAsync("RX-8");

        result!.AuditChainValid.Should().BeFalse();
        result.RejectionReason.Should().Be("Audit chain integrity check failed: possible data tampering.");
    }

    [Fact]
    public async Task Not_found_returns_null()
    {
        var (client, _) = Client(HttpStatusCode.NotFound, """{"detail":"Not Found"}""");

        var result = await client.VerifyPrescriptionAsync("RX-404");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Prescription_id_is_escaped_into_a_single_path_segment()
    {
        var (client, handler) = Client(HttpStatusCode.NotFound, "{}");

        await client.VerifyPrescriptionAsync("../admin");

        handler.RequestedPath.Should().Be("/api/v1/prescriptions/session-..%2Fadmin");
    }

    private static (HealthTechApiClient, StubHandler) Client(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://orchestrator:8000") };
        return (new HealthTechApiClient(http, NullLogger<HealthTechApiClient>.Instance), handler);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? RequestedPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestedPath = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
