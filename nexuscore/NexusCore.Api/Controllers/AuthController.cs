using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly JwtTokenService _jwtTokenService;
    private readonly IPasswordHasher<User> _passwordHasher;

    public AuthController(AppDbContext context, JwtTokenService jwtTokenService, IPasswordHasher<User> passwordHasher)
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
            token = _jwtTokenService.CreateToken(user),
            email = user.Email,
            name = user.Name,
            role = user.Role
        });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == dto.Email);
        if (user == null)
        {
            return Unauthorized(new { message = "Невірний email або пароль." });
        }

        var verificationResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, dto.Password);
        if (verificationResult == PasswordVerificationResult.Failed)
        {
            return Unauthorized(new { message = "Невірний email або пароль." });
        }

        return Ok(new
        {
            token = _jwtTokenService.CreateToken(user),
            email = user.Email,
            name = user.Name,
            role = user.Role
        });
    }
}

// DTO класи для передачі даних
public record RegisterDto(string Email, string Password, string Name);
public record LoginDto(string Email, string Password);