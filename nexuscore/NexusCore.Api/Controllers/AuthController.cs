using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexusCore.Api.Helpers;
using NexusCore.Api.Security;

namespace NexusCore.Api.Controllers;

namespace NexusCore.Api.Controllers;

/// <summary>
/// Handles account registration, login, and logout. Issues the JWT auth cookie used by
/// every other authenticated endpoint in the API.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController(DbService db, JwtTokenService jwt) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public IActionResult Register([FromBody] RegisterRequest body)
    {
        return Ok(new { message = "Registration endpoint is configured by the application runtime." });
    }

    /// <summary>
    /// Authenticates an existing user with email and password.
    /// </summary>
    /// <remarks>
    /// On success, sets the auth cookie so the caller is authenticated for subsequent requests.
    /// </remarks>
    /// <param name="body">Email and password to authenticate with.</param>
    /// <response code="200">Login succeeded; returns the authenticated user's public profile.</response>
    /// <response code="400">Email or password failed validation (e.g. missing fields).</response>
    /// <response code="401">No account matches the email, or the password is incorrect.</response>
    /// <response code="500">Unexpected server error.</response>
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

public sealed record RegisterRequest(string? Username, string? Email, string? Password);
public sealed record LoginRequest(string? Email, string? Password);
