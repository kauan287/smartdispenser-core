using FluentAssertions;
using System.Security.Cryptography;
using System.Text;

namespace SmartDispenser.UnitTests.Middleware;

/// <summary>
/// Tests for HMAC-SHA256 webhook signature validation logic.
/// These tests reproduce the exact algorithm from WebhookSignatureMiddleware
/// to verify correctness without needing the full ASP.NET Core pipeline.
/// </summary>
public sealed class PaymentWebhookValidationTests
{
    private const string TestSecret = "test-webhook-secret-32-chars-ok!";

    // ── Signature computation ─────────────────────────────────────────────────

    [Fact]
    public void ComputeHmacSha256_ProducesDeterministicResult()
    {
        var payload = """{"transactionId":"TX-001","amount":50.00}""";
        var sig1 = ComputeSignature(payload, TestSecret);
        var sig2 = ComputeSignature(payload, TestSecret);

        sig1.Should().Be(sig2);
        sig1.Should().HaveLength(64); // SHA-256 hex = 64 chars
    }

    [Fact]
    public void ComputeHmacSha256_DifferentPayload_ProducesDifferentSignature()
    {
        var sig1 = ComputeSignature("""{"amount":50.00}""", TestSecret);
        var sig2 = ComputeSignature("""{"amount":51.00}""", TestSecret);

        sig1.Should().NotBe(sig2);
    }

    [Fact]
    public void ComputeHmacSha256_DifferentSecret_ProducesDifferentSignature()
    {
        var payload = """{"transactionId":"TX-001"}""";
        var sig1 = ComputeSignature(payload, TestSecret);
        var sig2 = ComputeSignature(payload, "different-secret-totally-wrong!!");

        sig1.Should().NotBe(sig2);
    }

    // ── Signature validation ──────────────────────────────────────────────────

    [Fact]
    public void ValidateSignature_WithCorrectSignature_ReturnsTrue()
    {
        var payload = """{"transactionId":"TX-001","amount":50.00,"status":"confirmed"}""";
        var expectedSig = $"sha256={ComputeSignature(payload, TestSecret)}";

        var isValid = ValidateSignature(payload, expectedSig, TestSecret);

        isValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateSignature_WithTamperedPayload_ReturnsFalse()
    {
        var originalPayload = """{"amount":50.00}""";
        var tamperedPayload = """{"amount":999.99}"""; // Amount changed!
        var signatureForOriginal = $"sha256={ComputeSignature(originalPayload, TestSecret)}";

        // Validate tampered payload against original signature
        var isValid = ValidateSignature(tamperedPayload, signatureForOriginal, TestSecret);

        isValid.Should().BeFalse();
    }

    [Fact]
    public void ValidateSignature_WithWrongSecret_ReturnsFalse()
    {
        var payload = """{"amount":50.00}""";
        var signatureWithCorrectSecret = $"sha256={ComputeSignature(payload, TestSecret)}";

        var isValid = ValidateSignature(payload, signatureWithCorrectSecret, "wrong-secret!!");

        isValid.Should().BeFalse();
    }

    [Fact]
    public void ValidateSignature_WithMissingPrefix_ReturnsFalse()
    {
        var payload = """{"amount":50.00}""";
        // Missing "sha256=" prefix — common mistake from gateway providers
        var signatureWithoutPrefix = ComputeSignature(payload, TestSecret);

        var isValid = ValidateSignature(payload, signatureWithoutPrefix, TestSecret);

        isValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("sha256=")]
    public void ValidateSignature_WithInvalidSignatureFormats_ReturnsFalse(string invalidSig)
    {
        var payload = """{"amount":50.00}""";
        var isValid = ValidateSignature(payload, invalidSig, TestSecret);
        isValid.Should().BeFalse();
    }

    // ── Dispenser state machine ───────────────────────────────────────────────

    [Fact]
    public void Dispenser_WhenCreated_IsOfflineWithZeroInventory()
    {
        var d = SmartDispenser.Core.Entities.Dispenser.Create("Machine-01", "Floor 1", 50);
        d.Status.Should().Be(SmartDispenser.Core.Entities.DispenserStatus.Offline);
        d.InventoryCount.Should().Be(0);
        d.CanDispense().Should().BeFalse();
    }

    [Fact]
    public void Dispenser_WhenOnlineWithInventory_CanDispense()
    {
        var d = SmartDispenser.Core.Entities.Dispenser.Create("Machine-01", "Floor 1", 50);
        d.GoOnline();
        d.UpdateInventory(10);
        d.CanDispense().Should().BeTrue();
    }

    [Fact]
    public void Dispenser_WhenDispensing_CannotDispenseAgain()
    {
        var d = SmartDispenser.Core.Entities.Dispenser.Create("Machine-01", "Floor 1", 50);
        d.GoOnline();
        d.UpdateInventory(10);
        d.BeginDispensing(Guid.NewGuid());

        d.CanDispense().Should().BeFalse();
        d.Status.Should().Be(SmartDispenser.Core.Entities.DispenserStatus.Dispensing);
    }

    [Fact]
    public void Dispenser_AfterCompleteDispensing_InventoryDecrements()
    {
        var d = SmartDispenser.Core.Entities.Dispenser.Create("Machine-01", "Floor 1", 50);
        d.GoOnline();
        d.UpdateInventory(10);
        d.BeginDispensing(Guid.NewGuid());
        d.CompleteDispensing();

        d.InventoryCount.Should().Be(9);
        d.Status.Should().Be(SmartDispenser.Core.Entities.DispenserStatus.Idle);
    }

    [Fact]
    public void Dispenser_TransitionToInvalidState_ThrowsInvalidOperationException()
    {
        var d = SmartDispenser.Core.Entities.Dispenser.Create("Machine-01", "Floor 1", 50);
        // Machine is Offline — cannot begin dispensing
        var act = () => d.BeginDispensing(Guid.NewGuid());
        act.Should().Throw<InvalidOperationException>().WithMessage("*Offline*");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ComputeSignature(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(keyBytes);
        return Convert.ToHexString(hmac.ComputeHash(payloadBytes)).ToLowerInvariant();
    }

    private static bool ValidateSignature(string payload, string receivedSig, string secret)
    {
        if (string.IsNullOrEmpty(receivedSig) || !receivedSig.StartsWith("sha256="))
            return false;

        var expectedSig = $"sha256={ComputeSignature(payload, secret)}";
        var receivedBytes = Encoding.UTF8.GetBytes(receivedSig);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSig);

        return CryptographicOperations.FixedTimeEquals(receivedBytes, expectedBytes);
    }
}
