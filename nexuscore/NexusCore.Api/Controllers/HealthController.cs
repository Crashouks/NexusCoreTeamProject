using Microsoft.AspNetCore.Mvc;

namespace NexusCore.Api.Controllers;

/// <summary>
/// Basic liveness check for the API, used by monitoring, startup scripts, and load balancers.
/// </summary>
[ApiController]
[Route("api")]
public class HealthController : ControllerBase
{
    /// <summary>
    /// Reports that the API process is up and responding.
    /// </summary>
    /// <response code="200">The API is running.</response>
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Health() => Ok(new { status = "ok", name = "NexusCore" });
}