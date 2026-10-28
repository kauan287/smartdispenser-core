using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartDispenser.API.BackgroundServices;
using SmartDispenser.Core.Entities;
using SmartDispenser.Infrastructure.Data;
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
        var dispenserId = await RegisterDispenser(http);
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

    [SkippableFact]
    public async Task A_conflict_on_one_machine_does_not_stop_the_deadline_check_for_the_others()
    {
        Skip.IfNot(AppFactory.UsesPostgres, "The in-memory provider has no transactions, so a failed save can leave the request behind.");
        var conflict = new ChangeMachineBeforeFirstInterruption();
        await using var app = factory.WithWebHostBuilder(b => b
            .UseSetting("Dispense:DispatchDeadline", "00:00:01")
            .ConfigureTestServices(services => services.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(conflict))));
        conflict.Services = app.Services;
        var http = app.CreateClient();
        var (machineA, first) = await Dispatch(app, http);
        var (machineB, second) = await Dispatch(app, http);
        await using var _ = machineA;
        await using var __ = machineB;
        conflict.Targets = [first, second];
        var monitor = app.Services.GetServices<IHostedService>().OfType<HeartbeatMonitor>().Single();

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await monitor.CheckAsync(CancellationToken.None);

        var conflicted = conflict.Conflicted!.Value;
        var other = conflicted == first ? second : first;
        (await Status(http, conflicted)).Should().Be("Dispatched", "its save lost the race and is retried on the next check");
        (await Status(http, other)).Should().Be("Interrupted");

        await monitor.CheckAsync(CancellationToken.None);

        (await Status(http, conflicted)).Should().Be("Interrupted");
    }

    private async Task<(MachineClient Machine, Guid RequestId)> Dispatch(WebApplicationFactory<Program> app, HttpClient http)
    {
        var rx = UniqueId("RX");
        factory.Orchestrator.Returns(rx, ApprovedPrescription(rx));
        var machine = await MachineClient.ConnectAsync(app, await RegisterDispenser(http));
        await machine.ReportInventory(5);
        var initiate = await http.PostAsJsonAsync($"/api/dispensers/{machine.DispenserId}/dispense",
            new { prescriptionId = rx, patientIdentifier = "PATIENT-42" });
        var requestId = (await initiate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("dispenseRequestId").GetGuid();
        (await http.SendAsync(factory.SignedWebhook(PaymentJson(requestId, UniqueId("TX"))))).StatusCode.Should().Be(HttpStatusCode.OK);
        await machine.NextCommand(TimeSpan.FromSeconds(5));
        return (machine, requestId);
    }

    private static async Task<string?> Status(HttpClient http, Guid requestId) =>
        (await http.GetFromJsonAsync<JsonElement>($"/api/dispense-requests/{requestId}")).GetProperty("status").GetString();

    private sealed class ChangeMachineBeforeFirstInterruption : SaveChangesInterceptor
    {
        public IServiceProvider? Services { get; set; }
        public HashSet<Guid> Targets { get; set; } = [];
        public Guid? Conflicted { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var interrupted = eventData.Context!.ChangeTracker.Entries<DispenseRequest>()
                .FirstOrDefault(e => e.Entity.Status == DispenseRequestStatus.Interrupted && Targets.Contains(e.Entity.Id));
            if (interrupted is not null && Conflicted is null)
            {
                Conflicted = interrupted.Entity.Id;
                using var scope = Services!.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var dispenser = await db.Dispensers.SingleAsync(d => d.Id == interrupted.Entity.DispenserId, ct);
                dispenser.UpdateInventory(4);
                await db.SaveChangesAsync(ct);
            }
            return result;
        }
    }
}
