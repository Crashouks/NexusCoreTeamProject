using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace NexusCore.Api.Security;

/// <summary>Names of the rate-limit policies that can be applied with <c>[EnableRateLimiting("...")]</c>.</summary>
public static class RateLimitPolicies
{
    /// <summary>Login / register: brute-force protection. Keyed by client IP.</summary>
    public const string Auth = "auth";

    /// <summary>Money, uploads, outbound requests. Keyed by user id (falls back to IP).</summary>
    public const string Sensitive = "sensitive";

    /// <summary>Cloud-gaming agent callbacks (anonymous, authenticated by server id + password). Keyed by IP.</summary>
    public const string Agent = "agent";
}

/// <summary>Bound from the "RateLimiting" configuration section. All windows are fixed windows.</summary>
public sealed class RateLimitSettings
{
    /// <summary>Back-stop applied to every /api and /hubs request. Generous: the SPA polls.</summary>
    public int GlobalPermitLimit { get; set; } = 300;
    public int GlobalWindowSeconds { get; set; } = 60;

    /// <summary>10 requests / minute / client - see task requirement.</summary>
    public int SensitivePermitLimit { get; set; } = 10;
    public int SensitiveWindowSeconds { get; set; } = 60;

    public int AuthPermitLimit { get; set; } = 10;
    public int AuthWindowSeconds { get; set; } = 60;

    /// <summary>An agent heartbeats + polls every ~3 s (~40 req/min); leave head-room for several agents behind one IP.</summary>
    public int AgentPermitLimit { get; set; } = 180;
    public int AgentWindowSeconds { get; set; } = 60;
}

public static class RateLimitingExtensions
{
    public static IServiceCollection AddNexusRateLimiting(this IServiceCollection services, RateLimitSettings s)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

                await response.WriteAsJsonAsync(
                    new { error = "Too many requests. Please slow down and retry later.", code = "RATE_LIMITED" },
                    cancellationToken);
            };

            // Global back-stop (runs for every request in addition to any endpoint policy).
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                var path = ctx.Request.Path;
                var limited = path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs");
                if (!limited
                    || HttpMethods.IsOptions(ctx.Request.Method)           // CORS pre-flight
                    || path.StartsWithSegments("/api/health"))             // liveness probes
                    return RateLimitPartition.GetNoLimiter("exempt");

                return RateLimitPartition.GetFixedWindowLimiter("global:" + ClientKey(ctx),
                    _ => Window(s.GlobalPermitLimit, s.GlobalWindowSeconds));
            });

            options.AddPolicy(RateLimitPolicies.Sensitive, ctx =>
                RateLimitPartition.GetFixedWindowLimiter("sensitive:" + ClientKey(ctx),
                    _ => Window(s.SensitivePermitLimit, s.SensitiveWindowSeconds)));

            // Unauthenticated by definition - always keyed by IP, never by a claim the caller controls.
            options.AddPolicy(RateLimitPolicies.Auth, ctx =>
                RateLimitPartition.GetFixedWindowLimiter("auth:" + IpKey(ctx),
                    _ => Window(s.AuthPermitLimit, s.AuthWindowSeconds)));

            options.AddPolicy(RateLimitPolicies.Agent, ctx =>
                RateLimitPartition.GetFixedWindowLimiter("agent:" + IpKey(ctx),
                    _ => Window(s.AgentPermitLimit, s.AgentWindowSeconds)));
        });

        return services;
    }

    private static FixedWindowRateLimiterOptions Window(int permits, int seconds) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromSeconds(seconds),
        QueueLimit = 0,              // reject immediately instead of queueing (queued requests are still load)
        AutoReplenishment = true,
    };

    /// <summary>Authenticated user id when available, otherwise the client IP.</summary>
    internal static string ClientKey(HttpContext ctx)
    {
        var userId = ctx.User.FindFirst("userId")?.Value;
        return !string.IsNullOrEmpty(userId) && ctx.User.Identity?.IsAuthenticated == true
            ? "u:" + userId
            : IpKey(ctx);
    }

    internal static string IpKey(HttpContext ctx) =>
        "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
}
