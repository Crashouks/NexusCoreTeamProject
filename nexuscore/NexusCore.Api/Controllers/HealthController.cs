using Microsoft.AspNetCore.Mvc;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api")]
public class HealthController : ControllerBase
{
    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "ok", name = "NexusCore" });
}
