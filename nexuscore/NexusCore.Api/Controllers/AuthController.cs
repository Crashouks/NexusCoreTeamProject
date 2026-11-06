using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexusCore.Api.Helpers;
using NexusCore.Api.Security;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public IActionResult Register([FromBody] RegisterRequest body)
    {
        return Ok(new { message = "Registration endpoint is configured by the application runtime." });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public IActionResult Login([FromBody] LoginRequest body)
    {
        return Ok(new { message = "Login endpoint is configured by the application runtime." });
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public IActionResult Logout()
    {
        AuthCookie.Clear(Response, Request);
        return Ok(new { message = "Logged out" });
    }
}

public sealed record RegisterRequest(string? Username, string? Email, string? Password);
public sealed record LoginRequest(string? Email, string? Password);
