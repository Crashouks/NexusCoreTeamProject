using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using NexusCore.Api.Security;

namespace NexusCore.Api.Services;

public sealed class JwtTokenService(IConfiguration config, JwtSettings settings)
{
    public string CreateToken(dynamic user)
    {
        var expiry = ParseExpiry(
            Environment.GetEnvironmentVariable("JWT_EXPIRES_IN")
            ?? config["JWT_EXPIRES_IN"]
            ?? "1d");
        var claims = new List<Claim>
        {
            new("userId", user.user_id.ToString()),
            new("username", (string)user.username),
            new("role", (string)user.role),
            new("cloudPlan", (string)(user.cloud_plan ?? "free")),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        if (user.cloud_plan_expires is not null)
            claims.Add(new Claim("cloudPlanExpires", user.cloud_plan_expires.ToString()));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            settings.Issuer,
            settings.Audience,
            claims,
            notBefore: now,
            expires: now.Add(expiry),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public TimeSpan GetTokenLifetime() =>
        ParseExpiry(Environment.GetEnvironmentVariable("JWT_EXPIRES_IN") ?? config["JWT_EXPIRES_IN"] ?? "1d");

    private static TimeSpan ParseExpiry(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return TimeSpan.FromDays(1);
        var duration = value.Trim().ToLowerInvariant();
        if (duration.EndsWith('d') && double.TryParse(duration[..^1], out var days) && days > 0)
            return TimeSpan.FromDays(days);
        if (duration.EndsWith('h') && double.TryParse(duration[..^1], out var hours) && hours > 0)
            return TimeSpan.FromHours(hours);
        if (duration.EndsWith('m') && double.TryParse(duration[..^1], out var minutes) && minutes > 0)
            return TimeSpan.FromMinutes(minutes);
        if (duration.EndsWith('s') && double.TryParse(duration[..^1], out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.FromDays(1);
    }
}
