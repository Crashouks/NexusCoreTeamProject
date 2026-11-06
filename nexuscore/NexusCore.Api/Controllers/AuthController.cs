using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using NexusCore.Api.Security;
using Microsoft.AspNetCore.RateLimiting;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
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
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest body)
    {
        _context = context;
        _jwtTokenService = jwtTokenService;
        _passwordHasher = passwordHasher;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto)
    {
        if (await _context.Users.AnyAsync(u => u.Email == dto.Email))
        {
            return BadRequest(new { message = "Користувач із такою електронною поштою вже існує." });
        }

        var user = new User
        {
            Email = dto.Email,
            Name = dto.Name,
            Role = "User"
        };
        
        user.PasswordHash = _passwordHasher.HashPassword(user, dto.Password);

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return StatusCode(201, new
        {
            return ApiResults.ServerError(ex);
        }
    }

    [HttpPost("login")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> Login([FromBody] LoginRequest body)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == dto.Email);
        if (user == null)
        {
            return Unauthorized(new { message = "Невірний email або пароль." });
        }

        var verificationResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, dto.Password);
        if (verificationResult == PasswordVerificationResult.Failed)
        {
            return ApiResults.ServerError(ex);
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
    [AllowAnonymous]
    public IActionResult Logout()
    {
        AuthCookie.Clear(Response, Request);
        return Ok(new { message = "Logged out" });
    }
}

// DTO класи для передачі даних
public record RegisterDto(string Email, string Password, string Name);
public record LoginDto(string Email, string Password);