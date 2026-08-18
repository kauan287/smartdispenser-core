using MediatR;
using Microsoft.AspNetCore.Mvc;
using SmartDispenser.Core.Commands.InitiateDispense;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.API.Controllers;

/// <summary>
/// Dispenser machine management endpoints.
/// CRUD for machines + trigger dispense flows.
/// </summary>
[ApiController]
[Route("api/dispensers")]
[Produces("application/json")]
public sealed class DispensersController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IDispenserRepository _dispenserRepo;
    private readonly ILogger<DispensersController> _logger;

    public DispensersController(
        IMediator mediator,
        IDispenserRepository dispenserRepo,
        ILogger<DispensersController> logger)
    {
        _mediator = mediator;
        _dispenserRepo = dispenserRepo;
        _logger = logger;
    }

    /// <summary>List all registered dispensing machines with their current status.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<DispenserSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var dispensers = await _dispenserRepo.GetAllAsync(ct);
        var summaries = dispensers.Select(d => new DispenserSummary(
            d.Id, d.Name, d.Location,
            d.Status.ToString(),
            d.InventoryCount, d.InventoryCapacity,
            Math.Round(d.InventoryPercentage, 1),
            d.LastHeartbeatAt, d.LastDispenseAt,
            d.FirmwareVersion));

        return Ok(summaries);
    }

    /// <summary>Get a single dispenser by ID.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DispenserSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var dispenser = await _dispenserRepo.GetByIdAsync(id, ct);
        if (dispenser is null) return NotFound();

        return Ok(new DispenserSummary(
            dispenser.Id, dispenser.Name, dispenser.Location,
            dispenser.Status.ToString(),
            dispenser.InventoryCount, dispenser.InventoryCapacity,
            Math.Round(dispenser.InventoryPercentage, 1),
            dispenser.LastHeartbeatAt, dispenser.LastDispenseAt,
            dispenser.FirmwareVersion));
    }

    /// <summary>
    /// Initiate a dispense request for a specific machine.
    /// Prescription is verified against the HealthTech Agent Orchestrator before dispensing.
    /// </summary>
    [HttpPost("{id:guid}/dispense")]
    [ProducesResponseType(typeof(DispenseResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> InitiateDispense(
        Guid id,
        [FromBody] InitiateDispenseRequest request,
        CancellationToken ct)
    {
        var result = await _mediator.Send(
            new InitiateDispenseCommand(id, request.PrescriptionId, request.PatientIdentifier), ct);

        if (!result.Success)
        {
            _logger.LogWarning(
                "Dispense request denied for machine {Id}: {Reason}", id, result.ErrorMessage);

            return UnprocessableEntity(new ProblemDetails
            {
                Title = "Dispense request rejected",
                Detail = result.ErrorMessage,
                Status = StatusCodes.Status422UnprocessableEntity,
            });
        }

        return Accepted(new DispenseResponse(result.DispenseRequestId!.Value));
    }

    // ── Request/Response models ───────────────────────────────────────────────

    public sealed record InitiateDispenseRequest(
        string PrescriptionId,
        string PatientIdentifier);

    public sealed record DispenseResponse(Guid DispenseRequestId);

    public sealed record DispenserSummary(
        Guid Id, string Name, string Location,
        string Status,
        int InventoryCount, int InventoryCapacity,
        double InventoryPercentage,
        DateTimeOffset LastHeartbeatAt,
        DateTimeOffset? LastDispenseAt,
        string? FirmwareVersion);
}
