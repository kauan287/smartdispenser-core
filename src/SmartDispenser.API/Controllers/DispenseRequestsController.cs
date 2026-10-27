using Microsoft.AspNetCore.Mvc;
using SmartDispenser.Core.DTOs;
using SmartDispenser.Core.Interfaces;

namespace SmartDispenser.API.Controllers;

[ApiController]
[Route("api/dispense-requests")]
[Produces("application/json")]
public sealed class DispenseRequestsController(IDispenseRequestRepository requestRepo) : ControllerBase
{
    /// <summary>Get the status of a dispense request: PendingPayment, Dispatched, Completed or Failed.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<DispenseRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct) =>
        await requestRepo.GetByIdAsync(id, ct) is { } request ? Ok(DispenseRequestDto.From(request)) : NotFound();
}
