using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MySqlConnector;
using NexusCore.Api.Helpers;
using NexusCore.Api.Security;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

/// <summary>
/// Handles account registration, login, and logout. Issues the JWT auth cookie used by
/// every other authenticated endpoint in the API.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(DbService db, JwtTokenService jwt) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest body, CancellationToken cancellationToken)
    {
        var validationError = RegistrationValidator.ValidateRegister(body.Username, body.Email, body.Password);
        if (validationError is not null)
            return ApiResults.Error(StatusCodes.Status400BadRequest, validationError, "VALIDATION_ERROR");

        var username = body.Username!.Trim();
        var email = body.Email!.Trim();
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(body.Password, workFactor: 12);

        await using var connection = db.CreateConnection();
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO users (username, email, password, reg_date)
                VALUES (@username, @email, @passwordHash, NOW())
                """,
                new { username, email, passwordHash },
                cancellationToken: cancellationToken));
            var userId = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT LAST_INSERT_ID()",
                cancellationToken: cancellationToken));

            var token = jwt.CreateToken(userId, username, role: "user", cloudPlan: "free");
            AuthCookie.Set(Response, Request, token, jwt.TokenLifetime);
            return Ok(new { message = "Registration successful" });
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            return Conflict(new { code = "account_exists", message = "An account with that username or email already exists." });
        }
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest body, CancellationToken cancellationToken)
    {
        var validationError = RegistrationValidator.ValidateLogin(body.Email, body.Password);
        if (validationError is not null)
            return ApiResults.Error(StatusCodes.Status400BadRequest, validationError, "VALIDATION_ERROR");

        await using var connection = db.CreateConnection();
        var account = await connection.QuerySingleOrDefaultAsync<AuthAccount>(new CommandDefinition(
            """
            SELECT user_id AS UserId, username AS Username, email AS Email,
                   password AS PasswordHash, role AS Role, cloud_plan AS CloudPlan,
                   cloud_plan_expires AS CloudPlanExpires
            FROM users
            WHERE email = @email
            """,
            new { email = body.Email!.Trim() },
            cancellationToken: cancellationToken));

        if (account is null || !BCrypt.Net.BCrypt.Verify(body.Password, account.PasswordHash))
            return Unauthorized(new { code = "invalid_credentials", message = "Email or password is incorrect." });

        var token = jwt.CreateToken(
            account.UserId,
            account.Username,
            account.Role,
            account.CloudPlan,
            cloudPlanExpires: account.CloudPlanExpires);
        AuthCookie.Set(Response, Request, token, jwt.TokenLifetime);
        return Ok(new { message = "Login successful" });
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Logout()
    {
        AuthCookie.Clear(Response, Request);
        return Ok(new { message = "Logged out" });
    }

    private sealed class AuthAccount
    {
        public int UserId { get; init; }
        public required string Username { get; init; }
        public required string Email { get; init; }
        public required string PasswordHash { get; init; }
        public required string Role { get; init; }
        public required string CloudPlan { get; init; }
        public DateTime? CloudPlanExpires { get; init; }
    }
}

public sealed record RegisterRequest(string? Username, string? Email, string? Password);
public sealed record LoginRequest(string? Email, string? Password);
