using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace NexusCore.Api.Resilience;

/// <summary>
/// Tunables for calls to external image hosts. Bound from "Resilience:RemoteImageProbe".
/// Times are in milliseconds so tests can shrink them.
/// </summary>
public sealed class RemoteProbeOptions
{
    /// <summary>Upper bound for the whole operation, including all retries and back-off delays.</summary>
    public int TotalTimeoutMs { get; set; } = 10_000;

    /// <summary>Upper bound for one HTTP attempt.</summary>
    public int AttemptTimeoutMs { get; set; } = 3_000;

    /// <summary>Retries after the first attempt (0 disables retry).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Base delay; doubled on every retry (exponential back-off) with jitter.</summary>
    public int RetryDelayMs { get; set; } = 300;

    /// <summary>Open the circuit when this share of calls within the sampling window failed...</summary>
    public double BreakerFailureRatio { get; set; } = 0.5;

    /// <summary>...and at least this many calls were made in that window.</summary>
    public int BreakerMinimumThroughput { get; set; } = 5;

    public int BreakerSamplingMs { get; set; } = 30_000;

    /// <summary>How long the circuit stays open before a single trial request is let through (half-open).</summary>
    public int BreakDurationMs { get; set; } = 30_000;
}

public static class ResilienceExtensions
{
    public const string ImageProbePipeline = "remote-image-probe";

    /// <summary>
    /// Registers <see cref="IImageUrlProbe"/> with an SSRF-safe handler and the resilience pipeline
    /// (outermost to innermost): total Timeout -> Retry -> Circuit Breaker -> per-attempt Timeout.
    /// Backed by Polly v8 via Microsoft.Extensions.Http.Resilience.
    /// </summary>
    public static IHttpClientBuilder AddRemoteImageProbe(this IServiceCollection services, RemoteProbeOptions o)
    {
        services.AddSingleton(o);

        var client = services.AddHttpClient<IImageUrlProbe, ImageUrlProbe>(c =>
        {
            // Polly owns the timeouts; this is only a last-resort safety net above them.
            c.Timeout = TimeSpan.FromMilliseconds(o.TotalTimeoutMs + 5_000);
        });

        client.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            ConnectCallback = RemoteUrlGuard.GuardedConnectAsync, // SSRF guard, enforced on every hop
            UseProxy = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromMilliseconds(Math.Min(o.AttemptTimeoutMs, 3_000)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        });

        client.AddResilienceHandler(ImageProbePipeline, pipeline =>
        {
            // 1. Timeout - total budget for the whole call, retries included.
            pipeline.AddTimeout(new HttpTimeoutStrategyOptions
            {
                Name = "total-timeout",
                Timeout = TimeSpan.FromMilliseconds(o.TotalTimeoutMs),
            });

            // 2. Retry - only transient failures; HEAD is idempotent so repeating it is safe.
            if (o.MaxRetries > 0)
            {
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    Name = "retry",
                    MaxRetryAttempts = o.MaxRetries,
                    Delay = TimeSpan.FromMilliseconds(o.RetryDelayMs),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome)),
                });
            }

            // 3. Circuit Breaker - stop hammering a failing host; closed -> open -> half-open -> closed.
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                Name = "circuit-breaker",
                FailureRatio = o.BreakerFailureRatio,
                MinimumThroughput = o.BreakerMinimumThroughput,
                SamplingDuration = TimeSpan.FromMilliseconds(o.BreakerSamplingMs),
                BreakDuration = TimeSpan.FromMilliseconds(o.BreakDurationMs),
                ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome)),
            });

            // 4. Timeout - per attempt, so one hanging connection cannot eat the whole budget.
            pipeline.AddTimeout(new HttpTimeoutStrategyOptions
            {
                Name = "attempt-timeout",
                Timeout = TimeSpan.FromMilliseconds(o.AttemptTimeoutMs),
            });
        });

        return client;
    }

    /// <summary>
    /// What counts as a dependency failure: network errors, timeouts, 5xx, 408 and 429.
    /// Client mistakes (4xx) and SSRF-guard rejections are the caller's fault, so they neither
    /// trigger retries nor push the circuit breaker towards opening.
    /// </summary>
    internal static bool IsTransientFailure(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is { } ex)
        {
            if (RemoteUrlGuard.IsBlocked(ex)) return false;
            return ex is HttpRequestException or TimeoutRejectedException;
        }

        if (outcome.Result is { } response)
        {
            var code = (int)response.StatusCode;
            return code >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
        }

        return false;
    }
}
