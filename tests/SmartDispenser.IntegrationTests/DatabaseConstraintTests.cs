using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SmartDispenser.Core.Entities;
using SmartDispenser.Infrastructure.Data;
using Xunit;
using static SmartDispenser.IntegrationTests.DispenseFlowTests;

namespace SmartDispenser.IntegrationTests;

public sealed class DatabaseConstraintTests(AppFactory factory) : IClassFixture<AppFactory>
{
    [SkippableFact]
    public async Task Database_allows_one_live_request_per_prescription_even_when_the_app_check_is_bypassed()
    {
        Skip.IfNot(AppFactory.UsesPostgres, "The in-memory provider does not enforce unique indexes.");
        var rx = $"RX-{Guid.NewGuid():N}"[..12];

        await Save(DispenseRequest.Create(Guid.NewGuid(), rx, "PATIENT-1", "Amoxicillin", "500 mg"));
        var second = () => Save(DispenseRequest.Create(Guid.NewGuid(), rx, "PATIENT-2", "Amoxicillin", "500 mg"));

        await second.Should().ThrowAsync<DbUpdateException>();
    }

    [SkippableFact]
    public async Task Database_allows_a_new_request_once_the_earlier_one_failed()
    {
        Skip.IfNot(AppFactory.UsesPostgres, "The in-memory provider does not enforce unique indexes.");
        var rx = $"RX-{Guid.NewGuid():N}"[..12];
        var failed = DispenseRequest.Create(Guid.NewGuid(), rx, "PATIENT-1", "Amoxicillin", "500 mg");
        failed.MarkFailed("Payment failed");

        await Save(failed);
        await Save(DispenseRequest.Create(Guid.NewGuid(), rx, "PATIENT-1", "Amoxicillin", "500 mg"));

        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().DispenseRequests.CountAsync(r => r.PrescriptionId == rx))
            .Should().Be(2);
    }

    [SkippableFact]
    public async Task Database_keeps_a_prescription_locked_while_its_dispense_is_interrupted()
    {
        Skip.IfNot(AppFactory.UsesPostgres, "The in-memory provider does not enforce unique indexes.");
        var rx = $"RX-{Guid.NewGuid():N}"[..12];
        var interrupted = DispenseRequest.Create(Guid.NewGuid(), rx, "PATIENT-1", "Amoxicillin", "500 mg");
        interrupted.RecordPayment(Guid.NewGuid());
        interrupted.MarkDispatched();
        interrupted.MarkInterrupted();

        await Save(interrupted);
        var second = () => Save(DispenseRequest.Create(Guid.NewGuid(), rx, "PATIENT-2", "Amoxicillin", "500 mg"));

        await second.Should().ThrowAsync<DbUpdateException>();
    }

    [SkippableFact]
    public async Task Two_concurrent_reservations_of_one_prescription_answer_202_and_422()
    {
        Skip.IfNot(AppFactory.UsesPostgres, "The in-memory provider does not enforce unique indexes.");
        var rx = UniqueId("RX");
        factory.Orchestrator.Returns(rx, ApprovedPrescription(rx));
        var together = new SaveReservationsTogether(rx, writers: 2);
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(together))));
        var http = app.CreateClient();
        var machines = new List<MachineClient>();
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var machine = await MachineClient.ConnectAsync(app, await RegisterDispenser(http));
            await machine.ReportInventory(5);
            machines.Add(machine);
        }

        var responses = await Task.WhenAll(machines.Select(m => http.PostAsJsonAsync($"/api/dispensers/{m.DispenserId}/dispense",
            new { prescriptionId = rx, patientIdentifier = "PATIENT-42" })));

        responses.Select(r => (int)r.StatusCode).Order().Should().Equal(202, 422);
        (await responses.Single(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("detail").GetString().Should().Be($"Prescription '{rx}' already has a dispense request.");
        foreach (var machine in machines)
            await machine.DisposeAsync();
    }

    private sealed class SaveReservationsTogether(string prescriptionId, int writers) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<DispenseRequest>()
                .Any(e => e.State == EntityState.Added && e.Entity.PrescriptionId == prescriptionId))
            {
                if (Interlocked.Increment(ref _arrived) == writers)
                    _allArrived.SetResult();
                await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            return result;
        }
    }

    private async Task Save(DispenseRequest request)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.DispenseRequests.Add(request);
        await db.SaveChangesAsync();
    }
}
