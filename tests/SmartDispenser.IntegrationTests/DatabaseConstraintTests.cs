using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartDispenser.Core.Entities;
using SmartDispenser.Infrastructure.Data;
using Xunit;

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

    private async Task Save(DispenseRequest request)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.DispenseRequests.Add(request);
        await db.SaveChangesAsync();
    }
}
