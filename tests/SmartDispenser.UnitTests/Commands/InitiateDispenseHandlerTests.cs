using FluentAssertions;
using Moq;
using SmartDispenser.Core.Commands.InitiateDispense;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Entities;
using SmartDispenser.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using MediatR;

namespace SmartDispenser.UnitTests.Commands;

public sealed class InitiateDispenseHandlerTests
{
    private readonly Mock<IHealthTechApiClient> _healthTechMock = new();
    private readonly Mock<IDispenserRepository> _dispenserRepoMock = new();
    private readonly Mock<IDispenseRequestRepository> _requestRepoMock = new();
    private readonly Mock<IDispenserHubContext> _hubContextMock = new();

    private InitiateDispenseHandler CreateHandler() => new(
        _healthTechMock.Object,
        _dispenserRepoMock.Object,
        _requestRepoMock.Object,
        _hubContextMock.Object,
        NullLogger<InitiateDispenseHandler>.Instance);

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_WhenPrescriptionApprovedAndDispenserIdle_ReturnsSuccess()
    {
        // Arrange
        var dispenser = CreateIdleDispenser();
        SetupApprovedPrescription();
        _dispenserRepoMock.Setup(r => r.GetByIdAsync(dispenser.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dispenser);

        var handler = CreateHandler();
        var cmd = new InitiateDispenseCommand(dispenser.Id, "RX-001", "PATIENT-42");

        // Act
        var result = await handler.Handle(cmd, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.DispenseRequestId.Should().NotBeNull();
        result.ErrorMessage.Should().BeNull();

        _hubContextMock.Verify(h =>
            h.SendCommandAsync(dispenser.Id, It.IsAny<DispenserCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Prescription rejection paths ──────────────────────────────────────────

    [Fact]
    public async Task Handle_WhenPrescriptionPendingApproval_ReturnsFailure()
    {
        // Arrange
        var dispenser = CreateIdleDispenser();
        _dispenserRepoMock.Setup(r => r.GetByIdAsync(dispenser.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dispenser);

        _healthTechMock
            .Setup(c => c.VerifyPrescriptionAsync("RX-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrescriptionVerificationResult
            {
                PrescriptionId = "RX-001",
                ThreadId = "session-RX-001",
                IsApproved = false,
                AuditChainValid = true,
                SafetyApproved = true,
                IsPendingApproval = true,  // <-- still waiting for doctor
            });

        var handler = CreateHandler();
        var result = await handler.Handle(
            new InitiateDispenseCommand(dispenser.Id, "RX-001", "PATIENT-42"), CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("pending doctor approval");
        _hubContextMock.Verify(
            h => h.SendCommandAsync(It.IsAny<Guid>(), It.IsAny<DispenserCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_WhenAuditChainInvalid_ReturnsFailure()
    {
        // Arrange
        var dispenser = CreateIdleDispenser();
        _dispenserRepoMock.Setup(r => r.GetByIdAsync(dispenser.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dispenser);

        _healthTechMock
            .Setup(c => c.VerifyPrescriptionAsync("RX-002", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrescriptionVerificationResult
            {
                PrescriptionId = "RX-002",
                ThreadId = "session-RX-002",
                IsApproved = true,
                AuditChainValid = false,  // <-- tampered!
                SafetyApproved = true,
                IsPendingApproval = false,
            });

        var handler = CreateHandler();
        var result = await handler.Handle(
            new InitiateDispenseCommand(dispenser.Id, "RX-002", "PATIENT-42"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("audit chain");
        _hubContextMock.Verify(
            h => h.SendCommandAsync(It.IsAny<Guid>(), It.IsAny<DispenserCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Dispenser state paths ─────────────────────────────────────────────────

    [Fact]
    public async Task Handle_WhenDispenserIsOffline_ReturnsFailure()
    {
        // Arrange
        SetupApprovedPrescription();
        var offlineDispenser = CreateIdleDispenser();
        offlineDispenser.GoOffline(); // State: Offline

        _dispenserRepoMock.Setup(r => r.GetByIdAsync(offlineDispenser.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(offlineDispenser);

        var handler = CreateHandler();
        var result = await handler.Handle(
            new InitiateDispenseCommand(offlineDispenser.Id, "RX-003", "PATIENT-42"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not ready");
    }

    [Fact]
    public async Task Handle_WhenHealthTechApiThrows_ReturnsFailure()
    {
        // Arrange — simulate circuit breaker open / network error
        _healthTechMock
            .Setup(c => c.VerifyPrescriptionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Circuit breaker open"));

        var dispenser = CreateIdleDispenser();
        _dispenserRepoMock.Setup(r => r.GetByIdAsync(dispenser.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dispenser);

        var handler = CreateHandler();
        var result = await handler.Handle(
            new InitiateDispenseCommand(dispenser.Id, "RX-004", "PATIENT-42"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("unavailable");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dispenser CreateIdleDispenser()
    {
        var d = Dispenser.Create("TestMachine-01", "Pharmacy A", capacity: 50);
        d.GoOnline();
        d.UpdateInventory(30);
        return d;
    }

    private void SetupApprovedPrescription(string prescriptionId = "RX-001")
    {
        _healthTechMock
            .Setup(c => c.VerifyPrescriptionAsync(prescriptionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrescriptionVerificationResult
            {
                PrescriptionId = prescriptionId,
                ThreadId = $"session-{prescriptionId}",
                IsApproved = true,
                AuditChainValid = true,
                SafetyApproved = true,
                IsPendingApproval = false,
                ProposedMedication = "Paracetamol",
                Dosage = "500mg cada 8 horas",
            });
    }
}
