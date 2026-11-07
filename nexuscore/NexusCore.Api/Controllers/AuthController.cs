using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexusCore.Api.Helpers;
using NexusCore.Api.Security;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(DbService db, JwtTokenService jwt) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest body)
    {
        var validationError = RegistrationValidator.ValidateRegister(body.Username, body.Email, body.Password);
        if (validationError is not null)
            return ApiResults.Error(400, validationError, "VALIDATION_ERROR");

        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(body.Password, 10);
            var userId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO users (username, email, password, cloud_plan) VALUES (@username, @email, @password, 'free'); SELECT LAST_INSERT_ID();",
                new
                {
                    username = body.Username.Trim(),
                    email = body.Email.Trim().ToLowerInvariant(),
                    password = passwordHash
                });
            var user = await conn.QuerySingleAsync<dynamic>(
                "SELECT * FROM users WHERE user_id=@userId",
                new { userId });
            var token = jwt.CreateToken(user);
            AuthCookie.Set(Response, Request, token, jwt.GetTokenLifetime());
            return StatusCode(201, new { user = AuthUser(user) });
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1062)
        {
            return ApiResults.Error(409, "Username or email already exists", "DUPLICATE");
        }
        catch (Exception ex)
        {
            return ApiResults.ServerError(ex);
        }
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest body)
    {
        var validationError = RegistrationValidator.ValidateLogin(body.Email, body.Password);
        if (validationError is not null)
            return ApiResults.Error(400, validationError, "VALIDATION_ERROR");

        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var user = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM users WHERE email=@email",
                new { email = body.Email.Trim().ToLowerInvariant() });
            if (user is null || !BCrypt.Net.BCrypt.Verify(body.Password, (string)user.password))
                return ApiResults.Error(401, "Invalid credentials", "INVALID_CREDENTIALS");

            var token = jwt.CreateToken(user);
            AuthCookie.Set(Response, Request, token, jwt.GetTokenLifetime());
            return Ok(new { user = AuthUser(user) });
        }
        catch (Exception ex)
        {
            return ApiResults.ServerError(ex);
        }
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Logout()
    {
        AuthCookie.Clear(Response, Request);
        return Ok(new { message = "Logged out" });
    }

    private static object AuthUser(dynamic user) => new
    {
        userId = (int)user.user_id,
        username = (string)user.username,
        role = (string)user.role,
        cloudPlan = (string)user.cloud_plan,
        cloudPlanExpires = user.cloud_plan_expires
    };

    public sealed record RegisterRequest(string Username, string Email, string Password);
    public sealed record LoginRequest(string Email, string Password);
}
