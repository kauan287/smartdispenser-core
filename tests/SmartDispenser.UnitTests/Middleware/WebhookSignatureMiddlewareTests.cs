using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartDispenser.API.Middleware;

namespace SmartDispenser.UnitTests.Middleware;

public sealed class WebhookSignatureMiddlewareTests
{
    private const string Secret = "test-webhook-secret-32-chars-ok!";
    private const string Body = """{"transactionId":"TX-001","status":"confirmed"}""";

    // openssl dgst -sha256 -hmac 'test-webhook-secret-32-chars-ok!' of Body
    private const string ValidSignature = "sha256=77d46e02b1bdb15b3968850be3335c2d9735fb70d26e9ff0f497cf1193100e0c";

    [Fact]
    public async Task Valid_signature_reaches_the_controller_with_the_body_still_readable()
    {
        var (status, response, downstreamBody) = await Send("/api/payments/webhook", Body, ValidSignature);

        status.Should().Be(StatusCodes.Status200OK);
        response.Should().Be("downstream");
        downstreamBody.Should().Be(Body);
    }

    [Fact]
    public async Task Tampered_body_is_rejected_with_401()
    {
        var tampered = Body.Replace("confirmed", "CONFIRMED");

        var (status, response, downstreamBody) = await Send("/api/payments/webhook", tampered, ValidSignature);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        response.Should().Be("Invalid signature.");
        downstreamBody.Should().BeNull();
    }

    [Fact]
    public async Task Tampered_signature_is_rejected_with_401()
    {
        var tamperedSignature = ValidSignature[..^1] + "d";

        var (status, response, _) = await Send("/api/payments/webhook", Body, tamperedSignature);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        response.Should().Be("Invalid signature.");
    }

    [Theory]
    [InlineData("77d46e02b1bdb15b3968850be3335c2d9735fb70d26e9ff0f497cf1193100e0c")]
    [InlineData("sha256=")]
    [InlineData("invalid")]
    public async Task Malformed_signature_is_rejected_with_401(string signature)
    {
        var (status, response, _) = await Send("/api/payments/webhook", Body, signature);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        response.Should().Be("Invalid signature.");
    }

    [Fact]
    public async Task Missing_signature_header_is_rejected_with_401()
    {
        var (status, response, _) = await Send("/api/payments/webhook", Body, signature: null);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        response.Should().Be("Missing signature header.");
    }

    [Fact]
    public async Task Other_paths_pass_through_without_a_signature()
    {
        var (status, response, _) = await Send("/api/dispensers", Body, signature: null);

        status.Should().Be(StatusCodes.Status200OK);
        response.Should().Be("downstream");
    }

    [Fact]
    public async Task Body_larger_than_512_KB_is_rejected_with_413()
    {
        var (status, _, downstreamBody) = await Send("/api/payments/webhook", new string('x', 512 * 1024 + 1), ValidSignature);

        status.Should().Be(StatusCodes.Status413PayloadTooLarge);
        downstreamBody.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("short-secret")]
    public void Secret_shorter_than_32_characters_fails_at_startup(string secret)
    {
        var act = () => CreateMiddleware(secret, _ => Task.CompletedTask);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Webhook:Secret must be at least 32 characters.");
    }

    private static WebhookSignatureMiddleware CreateMiddleware(string secret, RequestDelegate next)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Webhook:Secret"] = secret })
            .Build();
        return new WebhookSignatureMiddleware(next, NullLogger<WebhookSignatureMiddleware>.Instance, configuration);
    }

    private static async Task<(int Status, string Response, string? DownstreamBody)> Send(
        string path, string body, string? signature)
    {
        string? downstreamBody = null;
        var middleware = CreateMiddleware(Secret, async ctx =>
        {
            downstreamBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            await ctx.Response.WriteAsync("downstream");
        });

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        if (signature is not null)
            context.Request.Headers["X-Signature-SHA256"] = signature;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var response = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, response, downstreamBody);
    }
}
