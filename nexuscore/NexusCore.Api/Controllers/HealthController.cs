using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using NexusCore.Api.Data;

namespace NexusCore.Api.Controllers;

/// <summary>
/// Basic liveness check for the API, used by monitoring, startup scripts, and load balancers.
/// </summary>
[ApiController]
[Route("api")]
public class HealthController(GameRepository games, ILogger<HealthController> logger) : ControllerBase
{
    /// <summary>
    /// Reports that the API process is up and responding.
    /// </summary>
    /// <response code="200">The API is running.</response>
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [AllowAnonymous]
    public IActionResult Health() => Ok(new { status = "ok", name = "NexusCore" });

    /// <summary>
    /// Confirms the API can reach MySQL with its restricted account and read the catalog.
    /// </summary>
    /// <response code="200">Database reachable; returns the approved game count.</response>
    /// <response code="503">Database unreachable or credentials rejected.</response>
    [HttpGet("health/db")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [AllowAnonymous]
    public async Task<IActionResult> HealthDb(CancellationToken ct)
    {
        try
        {
            return Ok(new { status = "ok", approvedGames = await games.CountApprovedAsync(ct) });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Database health check failed");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "db_unavailable" });
        }
    }
}