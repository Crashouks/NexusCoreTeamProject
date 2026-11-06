using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using NexusCore.Api.Helpers;

namespace NexusCore.Api.Security;

public static class AuthenticationExtensions
{
    /// <summary>Machine-readable error codes returned in the JSON body of 401/403 responses.</summary>
    public static class Codes
    {
        public const string Unauthenticated = "UNAUTHENTICATED"; // no token at all
        public const string TokenExpired = "TOKEN_EXPIRED";
        public const string TokenInvalid = "TOKEN_INVALID";      // bad signature, issuer, audience, malformed ...
        public const string Forbidden = "FORBIDDEN";             // valid token, insufficient permissions
    }

    public static IServiceCollection AddNexusAuthentication(this IServiceCollection services, JwtSettings jwt)
    {
        services.AddSingleton(jwt);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Keep JWT claim names as-is (userId, role) - default inbound mapping breaks role checks.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = jwt.ClockSkew,
                    RoleClaimType = "role",
                    NameClaimType = "username",
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (string.IsNullOrEmpty(context.Token)
                            && context.Request.Cookies.TryGetValue(AuthCookie.Name, out var cookieToken)
                            && !string.IsNullOrEmpty(cookieToken))
                        {
                            context.Token = cookieToken;
                            return Task.CompletedTask;
                        }

                        // SignalR/WebSockets cannot send an Authorization header from the browser.
                        var path = context.HttpContext.Request.Path;
                        if (string.IsNullOrEmpty(context.Token) && path.StartsWithSegments("/hubs"))
                        {
                            var accessToken = context.Request.Query["access_token"];
                            if (!string.IsNullOrEmpty(accessToken))
                                context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    },

                    // 401: distinguish "no token" / "expired" / "invalid" with a stable JSON body.
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        var (code, message) = context.AuthenticateFailure switch
                        {
                            null => (Codes.Unauthenticated, "Authentication required"),
                            SecurityTokenExpiredException => (Codes.TokenExpired, "Token has expired"),
                            _ => (Codes.TokenInvalid, "Invalid token"),
                        };

                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate = code == Codes.Unauthenticated
                            ? "Bearer"
                            : $"Bearer error=\"invalid_token\", error_description=\"{message}\"";
                        await context.Response.WriteAsJsonAsync(new { error = message, code });
                    },

                    // 403: authenticated, but role/policy requirements not met.
                    OnForbidden = async context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(
                            new { error = "Insufficient permissions", code = Codes.Forbidden });
                    },
                };
            });

        return services;
    }
}
