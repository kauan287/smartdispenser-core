using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using static SmartDispenser.IntegrationTests.DispenseFlowTests;

namespace SmartDispenser.IntegrationTests;

public sealed class FieldLengthTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private readonly HttpClient _http = factory.CreateClient();

    [Fact]
    public async Task Webhook_with_an_idempotency_key_longer_than_its_column_answers_400()
    {
        var (machine, requestId) = await Reserve();
        await using var _ = machine;
        var payment = PaymentJson(requestId, UniqueId("TX"))
            .Replace("\"idempotencyKey\":\"idem-", $"\"idempotencyKey\":\"{new string('k', 100)}");

        var response = await _http.SendAsync(factory.SignedWebhook(payment));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RequestStatus(requestId)).Should().Be("PendingPayment");
    }

    [Fact]
    public async Task Declined_payment_with_a_long_gateway_response_fails_the_request()
    {
        var (machine, requestId) = await Reserve();
        await using var _ = machine;
        var declined = JsonSerializer.Serialize(new
        {
            transactionId = UniqueId("TX"),
            idempotencyKey = UniqueId("idem"),
            dispenseRequestId = requestId.ToString(),
            amount = 12.50m,
            currency = "EUR",
            status = "declined",
            provider = "sandbox",
            gatewayResponse = new string('g', 1000),
            timestamp = "2026-10-27T12:00:00Z",
        });

        var response = await _http.SendAsync(factory.SignedWebhook(declined));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var request = await _http.GetFromJsonAsync<JsonElement>($"/api/dispense-requests/{requestId}");
        request.GetProperty("status").GetString().Should().Be("Failed");
        request.GetProperty("failureReason").GetString().Should().HaveLength(500).And.StartWith("Payment failed: ggg");
    }

    [Fact]
    public async Task Machine_failure_with_a_long_message_fails_the_request()
    {
        var (machine, requestId) = await Reserve();
        await using var _ = machine;
        await _http.SendAsync(factory.SignedWebhook(PaymentJson(requestId, UniqueId("TX"))));
        await machine.NextCommand(TimeSpan.FromSeconds(5));

        await machine.ReportDispenseFailure(requestId, new string('e', 600));

        (await RequestStatus(requestId)).Should().Be("Failed");
    }

    [Fact]
    public async Task Prescription_with_a_dosage_longer_than_its_column_is_rejected()
    {
        var rx = UniqueId("RX");
        factory.Orchestrator.Returns(rx, ApprovedPrescription(rx).Replace("500 mg every 8 hours", new string('d', 101)));
        var dispenserId = await RegisterDispenser(_http);
        await using var machine = await MachineClient.ConnectAsync(factory, dispenserId);
        await machine.ReportInventory(5);

        var response = await _http.PostAsJsonAsync($"/api/dispensers/{dispenserId}/dispense",
            new { prescriptionId = rx, patientIdentifier = "PATIENT-42" });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()
            .Should().Be("Prescription dosage is longer than 100 characters.");
    }

    private async Task<(MachineClient Machine, Guid RequestId)> Reserve()
    {
        var rx = UniqueId("RX");
        factory.Orchestrator.Returns(rx, ApprovedPrescription(rx));
        var machine = await MachineClient.ConnectAsync(factory, await RegisterDispenser(_http));
        await machine.ReportInventory(5);
        var initiate = await _http.PostAsJsonAsync($"/api/dispensers/{machine.DispenserId}/dispense",
            new { prescriptionId = rx, patientIdentifier = "PATIENT-42" });
        initiate.StatusCode.Should().Be(HttpStatusCode.Accepted);
        return (machine, (await initiate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("dispenseRequestId").GetGuid());
    }

    private async Task<string?> RequestStatus(Guid requestId) =>
        (await _http.GetFromJsonAsync<JsonElement>($"/api/dispense-requests/{requestId}")).GetProperty("status").GetString();
}
