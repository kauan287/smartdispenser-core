using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SmartDispenser.Core.Entities;
using SmartDispenser.Core.Interfaces;
using Xunit;

namespace SmartDispenser.IntegrationTests;

public sealed class DispenseFlowTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private const string ApprovedPrescription = """
        {
          "thread_id": "session-RX-100",
          "prescription_id": "RX-100",
          "proposed_medication": "Amoxicillin",
          "dosage": "500 mg every 8 hours",
          "safety_approved": true,
          "risk_notes": null,
          "human_approved": true,
          "doctor_comments": "Approved",
          "is_paused_waiting_approval": false,
          "audit_chain_valid": true
        }
        """;

    private readonly HttpClient _http = factory.CreateClient();

    [Fact]
    public async Task Machine_receives_the_dispense_command_only_after_payment_is_confirmed()
    {
        factory.Orchestrator.Returns("RX-100", ApprovedPrescription);
        var dispenserId = await RegisterDispenser();
        await using var machine = await MachineClient.ConnectAsync(factory, dispenserId);
        await machine.ReportInventory(5);

        var initiate = await _http.PostAsJsonAsync($"/api/dispensers/{dispenserId}/dispense",
            new { prescriptionId = "RX-100", patientIdentifier = "PATIENT-42" });
        initiate.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var requestId = (await initiate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("dispenseRequestId").GetGuid();

        await Task.Delay(1000);
        machine.Commands.Should().BeEmpty("nothing may be dispensed before the payment is confirmed");

        var webhook = await _http.SendAsync(factory.SignedWebhook(PaymentJson(requestId, "TX-100")));
        webhook.StatusCode.Should().Be(HttpStatusCode.OK);

        var command = await machine.NextCommand(TimeSpan.FromSeconds(5));
        command.GetProperty("requestId").GetGuid().Should().Be(requestId);
        command.GetProperty("medication").GetString().Should().Be("Amoxicillin");
        command.GetProperty("dosage").GetString().Should().Be("500 mg every 8 hours");

        await machine.ConfirmDispense(requestId);
        await Task.Delay(500);
        machine.Commands.Should().BeEmpty("a single payment dispenses exactly once");

        var dispenser = await _http.GetFromJsonAsync<JsonElement>($"/api/dispensers/{dispenserId}");
        dispenser.GetProperty("status").GetString().Should().Be("Idle");
        dispenser.GetProperty("inventoryCount").GetInt32().Should().Be(4);
    }

    [Fact]
    public async Task Unknown_prescription_is_rejected_as_not_found()
    {
        var dispenserId = await RegisterDispenser();

        var response = await _http.PostAsJsonAsync($"/api/dispensers/{dispenserId}/dispense",
            new { prescriptionId = "RX-404", patientIdentifier = "PATIENT-42" });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()
            .Should().Be("Prescription 'RX-404' was not found in the HealthTech Orchestrator.");
    }

    [Fact]
    public async Task A_prescription_cannot_be_dispensed_on_a_second_machine()
    {
        factory.Orchestrator.Returns("RX-200", ApprovedPrescription.Replace("RX-100", "RX-200"));
        var first = await RegisterDispenser();
        var second = await RegisterDispenser();
        await using var firstMachine = await MachineClient.ConnectAsync(factory, first);
        await using var secondMachine = await MachineClient.ConnectAsync(factory, second);
        await firstMachine.ReportInventory(5);
        await secondMachine.ReportInventory(5);

        var accepted = await _http.PostAsJsonAsync($"/api/dispensers/{first}/dispense",
            new { prescriptionId = "RX-200", patientIdentifier = "PATIENT-42" });
        var reused = await _http.PostAsJsonAsync($"/api/dispensers/{second}/dispense",
            new { prescriptionId = "RX-200", patientIdentifier = "PATIENT-42" });

        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        reused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await reused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()
            .Should().Be("Prescription 'RX-200' already has a dispense request.");
    }

    [Fact]
    public async Task Dashboard_renders()
    {
        var response = await _http.GetAsync("/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<Guid> RegisterDispenser()
    {
        using var scope = factory.Services.CreateScope();
        var dispenser = Dispenser.Create($"Machine-{Guid.NewGuid():N}"[..16], "Pharmacy A", 10);
        await scope.ServiceProvider.GetRequiredService<IDispenserRepository>().AddAsync(dispenser);
        return dispenser.Id;
    }

    private static string PaymentJson(Guid requestId, string transactionId) => JsonSerializer.Serialize(new
    {
        transactionId,
        idempotencyKey = $"idem-{transactionId}",
        dispenseRequestId = requestId.ToString(),
        amount = 12.50m,
        currency = "EUR",
        status = "confirmed",
        provider = "sandbox",
        timestamp = "2026-10-27T12:00:00Z",
    });
}
