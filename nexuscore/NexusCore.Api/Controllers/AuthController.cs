using BCrypt.Net;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

/// <summary>
/// Handles account registration, login, and logout. Issues the JWT auth cookie used by
/// every other authenticated endpoint in the API.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController(DbService db, JwtTokenService jwt) : ControllerBase
{
    /// <summary>
    /// Registers a new user account and immediately logs the user in.
    /// </summary>
    /// <remarks>
    /// On success, sets the auth cookie (see <see cref="AuthCookie"/>) so the caller is
    /// authenticated without a separate login call. The new account is created with the
    /// "free" cloud plan.
    /// </remarks>
    /// <param name="body">Username, email, and password for the new account.</param>
    /// <response code="201">Account created; returns the new user's public profile.</response>
    /// <response code="400">The username, email, or password failed validation.</response>
    /// <response code="409">The username or email is already registered.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost("register")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest body)
    {
        var validationError = RegistrationValidator.ValidateRegister(body.Username, body.Email, body.Password);
        if (validationError != null)
            return ApiResults.Error(400, validationError, "VALIDATION_ERROR");

        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var hash = BCrypt.Net.BCrypt.HashPassword(body.Password, 10);
            var id = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO users (username, email, password, cloud_plan) VALUES (@u, @e, @p, 'free'); SELECT LAST_INSERT_ID();",
                new { u = body.Username.Trim(), e = body.Email.Trim().ToLowerInvariant(), p = hash });
            var user = await conn.QuerySingleAsync<dynamic>("SELECT * FROM users WHERE user_id=@id", new { id });
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
            return ApiResults.Error(500, ex.Message, "SERVER_ERROR");
        }
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Login([FromBody] LoginRequest body)
    {
        var validationError = RegistrationValidator.ValidateLogin(body.Email, body.Password);
        if (validationError != null)
            return ApiResults.Error(400, validationError, "VALIDATION_ERROR");

        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var users = (await conn.QueryAsync<dynamic>(
                "SELECT * FROM users WHERE email=@e", new { e = body.Email.Trim().ToLowerInvariant() })).ToList();
            if (users.Count == 0) return ApiResults.Error(401, "Invalid credentials", "INVALID_CREDENTIALS");
            var user = users[0];
            if (!BCrypt.Net.BCrypt.Verify(body.Password, (string)user.password))
                return ApiResults.Error(401, "Invalid credentials", "INVALID_CREDENTIALS");
            var token = jwt.CreateToken(user);
            AuthCookie.Set(Response, Request, token, jwt.GetTokenLifetime());
            return Ok(new { user = AuthUser(user) });
        }
        catch (Exception ex)
        {
            return ApiResults.Error(500, ex.Message, "SERVER_ERROR");
        }
    }

    /// <summary>
    /// Logs the current caller out by clearing the auth cookie.
    /// </summary>
    /// <remarks>
    /// Always succeeds, even if the caller was not authenticated to begin with.
    /// </remarks>
    /// <response code="200">Auth cookie cleared.</response>
    [HttpPost("logout")]
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

    /// <summary>Request body for <see cref="Register"/>.</summary>
    /// <param name="Username">Desired username.</param>
    /// <param name="Email">Account email address.</param>
    /// <param name="Password">Account password (plain text; hashed server-side).</param>
    public record RegisterRequest(string Username, string Email, string Password);

    /// <summary>Request body for <see cref="Login"/>.</summary>
    /// <param name="Email">Account email address.</param>
    /// <param name="Password">Account password.</param>
    public record LoginRequest(string Email, string Password);
}