using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SmartDispenser.Core.Entities;
using SmartDispenser.Infrastructure.Data;
using Xunit;
using static SmartDispenser.IntegrationTests.DispenseFlowTests;

namespace SmartDispenser.IntegrationTests;

public sealed class WebhookConcurrencyTests(AppFactory factory) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Webhook_that_loses_a_race_for_the_machine_answers_409()
    {
        await using var race = await Race.Start(factory);

        var lost = await race.Http.SendAsync(factory.SignedWebhook(race.Payment));

        lost.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await lost.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()
            .Should().Be("Another command changed the same data first. Nothing was saved. Retry the webhook.");
        race.Machine.Commands.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Retry_of_a_webhook_that_lost_a_race_dispatches()
    {
        Skip.IfNot(AppFactory.UsesPostgres, "The in-memory provider has no transactions, so a failed save can leave the payment behind.");
        await using var race = await Race.Start(factory);
        await race.Http.SendAsync(factory.SignedWebhook(race.Payment));

        var retried = await race.Http.SendAsync(factory.SignedWebhook(race.Payment));

        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        (await retried.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("duplicate").GetBoolean().Should().BeFalse();
        (await race.Machine.NextCommand(TimeSpan.FromSeconds(5))).GetProperty("requestId").GetGuid().Should().Be(race.RequestId);
        var dispenser = await race.Http.GetFromJsonAsync<JsonElement>($"/api/dispensers/{race.DispenserId}");
        dispenser.GetProperty("status").GetString().Should().Be("Dispensing");
        dispenser.GetProperty("inventoryCount").GetInt32().Should().Be(9);
    }

    private sealed class Race : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> _app;

        private Race(WebApplicationFactory<Program> app, MachineClient machine, Guid dispenserId, Guid requestId)
        {
            _app = app;
            Http = app.CreateClient();
            Machine = machine;
            DispenserId = dispenserId;
            RequestId = requestId;
            Payment = PaymentJson(requestId, UniqueId("TX"));
        }

        public HttpClient Http { get; }
        public MachineClient Machine { get; }
        public Guid DispenserId { get; }
        public Guid RequestId { get; }
        public string Payment { get; }

        public static async Task<Race> Start(AppFactory factory)
        {
            var restock = new RestockBeforeFirstPaymentSave();
            var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
                services.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(restock))));
            restock.Services = app.Services;
            var http = app.CreateClient();
            var rx = UniqueId("RX");
            factory.Orchestrator.Returns(rx, ApprovedPrescription(rx));
            var dispenserId = await RegisterDispenser(http);
            var machine = await MachineClient.ConnectAsync(app, dispenserId);
            await machine.ReportInventory(5);
            var initiate = await http.PostAsJsonAsync($"/api/dispensers/{dispenserId}/dispense",
                new { prescriptionId = rx, patientIdentifier = "PATIENT-42" });
            var requestId = (await initiate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("dispenseRequestId").GetGuid();
            return new Race(app, machine, dispenserId, requestId);
        }

        public async ValueTask DisposeAsync()
        {
            await Machine.DisposeAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class RestockBeforeFirstPaymentSave : SaveChangesInterceptor
    {
        private bool _fired;

        public IServiceProvider? Services { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var paid = eventData.Context!.ChangeTracker.Entries<DispenseRequest>()
                .FirstOrDefault(e => e.State == EntityState.Modified && e.Entity.PaymentId is not null);
            if (paid is not null && !_fired)
            {
                _fired = true;
                using var scope = Services!.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var dispenser = await db.Dispensers.SingleAsync(d => d.Id == paid.Entity.DispenserId, ct);
                dispenser.UpdateInventory(9);
                await db.SaveChangesAsync(ct);
            }
            return result;
        }
    }
}
