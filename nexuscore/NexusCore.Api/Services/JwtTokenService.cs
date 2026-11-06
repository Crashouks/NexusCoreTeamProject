using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using NexusCore.Api.Security;

namespace NexusCore.Api.Services;

public sealed class JwtTokenService(IConfiguration config, JwtSettings settings)
{
    public string CreateToken(int userId, string? username = null, string? role = null, string? cloudPlan = null, TimeSpan? lifetime = null)
    {
        var claims = new List<Claim>
        {
            new("userId", userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        if (!string.IsNullOrWhiteSpace(username)) claims.Add(new Claim("username", username));
        if (!string.IsNullOrWhiteSpace(role)) claims.Add(new Claim("role", role));
        if (!string.IsNullOrWhiteSpace(cloudPlan)) claims.Add(new Claim("cloudPlan", cloudPlan));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var expiry = lifetime ?? ParseExpiry(Environment.GetEnvironmentVariable("JWT_EXPIRES_IN") ?? config["JWT_EXPIRES_IN"] ?? "1d");
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience, claims, now, now.Add(expiry), creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static TimeSpan ParseExpiry(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return TimeSpan.FromDays(1);

        var raw = value.Trim();
        if (raw.EndsWith("d", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromDays(double.Parse(raw[..^1]));
        if (raw.EndsWith("h", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromHours(double.Parse(raw[..^1]));
        if (raw.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMinutes(double.Parse(raw[..^1]));
        if (raw.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromSeconds(double.Parse(raw[..^1]));

        return TimeSpan.FromDays(1);
    }
}
