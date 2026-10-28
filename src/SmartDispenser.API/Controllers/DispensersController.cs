using System.ComponentModel.DataAnnotations;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using SmartDispenser.Core.Commands.InitiateDispense;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Entities;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.API.Controllers;

[ApiController]
[Route("api/dispensers")]
[Produces("application/json")]
public sealed class DispensersController(IMediator mediator, IDispenserRepository dispenserRepo, IUnitOfWork unitOfWork) : ControllerBase
{
    /// <summary>List all registered machines with their current state.</summary>
    [HttpGet]
    [ProducesResponseType<IEnumerable<DispenserStateDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken ct) =>
        Ok((await dispenserRepo.GetAllAsync(ct)).Select(DispenserStateDto.From));

    /// <summary>Get one machine by id.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<DispenserStateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct) =>
        await dispenserRepo.GetByIdAsync(id, ct) is { } dispenser ? Ok(DispenserStateDto.From(dispenser)) : NotFound();

    /// <summary>Register a machine and the one medication it stocks. It stays Offline until it connects to the hub with its id.</summary>
    [HttpPost]
    [ProducesResponseType<DispenserStateDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register([FromBody] RegisterDispenserRequest request, CancellationToken ct)
    {
        var dispenser = Dispenser.Create(request.Name, request.Location, request.Medication, request.InventoryCapacity);
        dispenserRepo.Add(dispenser);
        await unitOfWork.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetById), new { id = dispenser.Id }, DispenserStateDto.From(dispenser));
    }

    /// <summary>
    /// Verify a prescription with the HealthTech Agent Orchestrator and reserve it on this machine.
    /// The prescription's medication must match the one the machine stocks, ignoring case.
    /// The machine dispenses only after the payment webhook confirms the returned request id.
    /// </summary>
    [HttpPost("{id:guid}/dispense")]
    [ProducesResponseType<DispenseResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> InitiateDispense(Guid id, [FromBody] InitiateDispenseRequest request, CancellationToken ct)
    {
        var result = await mediator.Send(new InitiateDispenseCommand(id, request.PrescriptionId, request.PatientIdentifier), ct);

        return result.Error switch
        {
            null => AcceptedAtAction(nameof(DispenseRequestsController.GetById), "DispenseRequests",
                new { id = result.DispenseRequestId }, new DispenseResponse(result.DispenseRequestId!.Value)),
            InitiateDispenseError.DispenserNotFound => Problem(result.ErrorMessage, statusCode: StatusCodes.Status404NotFound),
            InitiateDispenseError.VerificationUnavailable => Problem(result.ErrorMessage, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Problem(result.ErrorMessage, title: "Dispense request rejected", statusCode: StatusCodes.Status422UnprocessableEntity),
        };
    }

    public sealed record RegisterDispenserRequest(
        [Required, MaxLength(100)] string Name,
        [Required, MaxLength(200)] string Location,
        [Required, MaxLength(200)] string Medication,
        [Range(1, 10_000)] int InventoryCapacity);

    public sealed record InitiateDispenseRequest(
        [Required, MaxLength(100)] string PrescriptionId,
        [Required, MaxLength(100)] string PatientIdentifier);

    public sealed record DispenseResponse(Guid DispenseRequestId);
}
