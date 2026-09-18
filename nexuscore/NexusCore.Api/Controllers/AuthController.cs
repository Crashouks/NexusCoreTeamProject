using BCrypt.Net;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(DbService db, JwtTokenService jwt) : ControllerBase
{
    [HttpPost("register")]
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

    [HttpPost("login")]
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

    [HttpPost("logout")]
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

    public record RegisterRequest(string Username, string Email, string Password);
    public record LoginRequest(string Email, string Password);
}
