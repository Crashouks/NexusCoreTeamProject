using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using NexusCore.Api.Security;

namespace NexusCore.Api.Services;

public sealed class JwtTokenService(IConfiguration config, JwtSettings settings)
{
    public TimeSpan TokenLifetime => GetTokenLifetime();

    public TimeSpan GetTokenLifetime() => ParseExpiry(
        Environment.GetEnvironmentVariable("JWT_EXPIRES_IN")
        ?? config["JWT_EXPIRES_IN"]
        ?? "1d");

    public static TimeSpan ParseExpiry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return TimeSpan.FromDays(1);

        var raw = value.Trim();
        var unit = char.ToLowerInvariant(raw[^1]);
        if (double.TryParse(raw[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            && double.IsFinite(amount)
            && amount > 0)
        {
            try
            {
                var duration = unit switch
                {
                    'd' => TimeSpan.FromDays(amount),
                    'h' => TimeSpan.FromHours(amount),
                    'm' => TimeSpan.FromMinutes(amount),
                    's' => TimeSpan.FromSeconds(amount),
                    _ => TimeSpan.Zero
                };
                if (duration > TimeSpan.Zero) return duration;
            }
            catch (ArgumentOutOfRangeException)
            {
                return TimeSpan.FromDays(1);
            }
        }

        return TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var parsed) && parsed > TimeSpan.Zero
            ? parsed
            : TimeSpan.FromDays(1);
    }

    public string CreateToken(
        int userId,
        string? username = null,
        string? role = null,
        string? cloudPlan = null,
        TimeSpan? lifetime = null,
        DateTime? cloudPlanExpires = null)
    {
        var claims = new List<Claim>
        {
            new("userId", userId.ToString(CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        if (!string.IsNullOrWhiteSpace(username)) claims.Add(new Claim("username", username));
        if (!string.IsNullOrWhiteSpace(role)) claims.Add(new Claim("role", role));
        claims.Add(new Claim("cloudPlan", string.IsNullOrWhiteSpace(cloudPlan) ? "free" : cloudPlan));
        if (cloudPlanExpires.HasValue)
            claims.Add(new Claim("cloudPlanExpires", cloudPlanExpires.Value.ToString()));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            settings.Issuer,
            settings.Audience,
            claims,
            now,
            now.Add(lifetime ?? TokenLifetime),
            credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreateToken(object user)
    {
        var values = user as IDictionary<string, object?>
            ?? throw new ArgumentException("The user must expose snake_case identity fields.", nameof(user));
        object? Get(string name) => values.TryGetValue(name, out var value) ? value : null;

        var id = Get("user_id") ?? throw new ArgumentException("The user_id field is required.", nameof(user));
        var userId = Convert.ToInt32(id, CultureInfo.InvariantCulture);
        var expires = Get("cloud_plan_expires") is DateTime date ? date : (DateTime?)null;
        return CreateToken(
            userId,
            Convert.ToString(Get("username"), CultureInfo.InvariantCulture),
            Convert.ToString(Get("role"), CultureInfo.InvariantCulture),
            Convert.ToString(Get("cloud_plan"), CultureInfo.InvariantCulture),
            cloudPlanExpires: expires);
    }
}
