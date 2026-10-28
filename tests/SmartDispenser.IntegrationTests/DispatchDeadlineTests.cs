using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartDispenser.API.BackgroundServices;
using Xunit;
using static SmartDispenser.IntegrationTests.DispenseFlowTests;

namespace SmartDispenser.IntegrationTests;

public sealed class DispatchDeadlineTests(AppFactory factory) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Dispense_the_machine_never_confirms_is_interrupted_at_the_deadline()
    {
        await using var app = factory.WithWebHostBuilder(b => b.UseSetting("Dispense:DispatchDeadline", "00:00:01"));
        var http = app.CreateClient();
        var rx = UniqueId("RX");
        factory.Orchestrator.Returns(rx, ApprovedPrescription(rx));
        var registered = await http.PostAsJsonAsync("/api/dispensers",
            new { name = $"Machine-{Guid.NewGuid():N}"[..16], location = "Pharmacy A", inventoryCapacity = 10 });
        var dispenserId = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var machine = await MachineClient.ConnectAsync(app, dispenserId);
        await machine.ReportInventory(5);
        var initiate = await http.PostAsJsonAsync($"/api/dispensers/{dispenserId}/dispense",
            new { prescriptionId = rx, patientIdentifier = "PATIENT-42" });
        var requestId = (await initiate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("dispenseRequestId").GetGuid();
        (await http.SendAsync(factory.SignedWebhook(PaymentJson(requestId, UniqueId("TX"))))).StatusCode.Should().Be(HttpStatusCode.OK);
        await machine.NextCommand(TimeSpan.FromSeconds(5));
        var monitor = app.Services.GetServices<IHostedService>().OfType<HeartbeatMonitor>().Single();

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await monitor.CheckAsync(CancellationToken.None);

        (await Status($"/api/dispense-requests/{requestId}")).Should().Be("Interrupted");
        (await Status($"/api/dispensers/{dispenserId}")).Should().Be("Error");

        await machine.ConfirmDispense(requestId);

        (await Status($"/api/dispense-requests/{requestId}")).Should().Be("Completed");
        var dispenser = await http.GetFromJsonAsync<JsonElement>($"/api/dispensers/{dispenserId}");
        dispenser.GetProperty("status").GetString().Should().Be("Error");
        dispenser.GetProperty("inventoryCount").GetInt32().Should().Be(4);

        async Task<string?> Status(string path) =>
            (await http.GetFromJsonAsync<JsonElement>(path)).GetProperty("status").GetString();
    }
}
