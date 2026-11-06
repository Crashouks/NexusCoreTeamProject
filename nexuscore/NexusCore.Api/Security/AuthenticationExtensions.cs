using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using NexusCore.Api.Helpers;

namespace NexusCore.Api.Security;

public static class AuthenticationExtensions
{
    public static class Codes
    {
        public const string Unauthenticated = "UNAUTHENTICATED";
        public const string TokenExpired = "TOKEN_EXPIRED";
        public const string TokenInvalid = "TOKEN_INVALID";
        public const string Forbidden = "FORBIDDEN";
    }

    public static IServiceCollection AddNexusAuthentication(this IServiceCollection services, JwtSettings jwt)
    {
        services.AddSingleton(jwt);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
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

                        if (string.IsNullOrEmpty(context.Token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        {
                            var accessToken = context.Request.Query["access_token"];
                            if (!string.IsNullOrEmpty(accessToken))
                                context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    },
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        var (code, message) = context.AuthenticateFailure switch
                        {
                            null => (Codes.Unauthenticated, "Authentication required"),
                            SecurityTokenExpiredException => (Codes.TokenExpired, "Token has expired"),
                            _ => (Codes.TokenInvalid, "Invalid token")
                        };

                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate = code == Codes.Unauthenticated
                            ? "Bearer"
                            : $"Bearer realm=\"{context.Request.Host}\", error=\"invalid_token\", error_description=\"{message}\"";

                        await context.Response.WriteAsJsonAsync(new { error = message, code });
                    },
                    OnForbidden = async context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new { error = "Insufficient permissions", code = Codes.Forbidden });
                    }
                };
            });

        return services;
    }
}
