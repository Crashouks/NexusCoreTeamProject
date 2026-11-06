using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using NexusCore.Api.Security;

namespace NexusCore.Api.Services;

public class JwtTokenService(IConfiguration config, JwtSettings settings)
{
    private readonly IConfiguration _config;
    private readonly SymmetricSecurityKey _key;

    public JwtTokenService(IConfiguration config)
    {
        var expiresIn = Environment.GetEnvironmentVariable("JWT_EXPIRES_IN") ?? config["JWT_EXPIRES_IN"] ?? "1d";
        var expiry = ParseExpiry(expiresIn);

    public string CreateToken(User user)
    {
        var claims = new List<Claim>
        {
            new("userId", user.user_id.ToString()),
            new("username", (string)user.username),
            new("role", (string)user.role),
            new("cloudPlan", (string)(user.cloud_plan ?? "free")),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience, claims, notBefore: now, expires: now.Add(expiry), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddDays(7),
            SigningCredentials = creds,
            Issuer = _config["Jwt:Issuer"],
            Audience = _config["Jwt:Audience"]
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);

        return tokenHandler.WriteToken(token);
    }
}