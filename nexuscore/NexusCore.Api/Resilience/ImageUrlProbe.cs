using System.Net;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace NexusCore.Api.Resilience;

public enum ProbeStatus
{
    /// <summary>Reachable and Content-Type is image/*.</summary>
    Image,
    /// <summary>Reachable but not an image.</summary>
    NotImage,
    /// <summary>Malformed URL, non-http(s), or contains credentials.</summary>
    InvalidUrl,
    /// <summary>Destination is private/loopback/link-local (SSRF guard).</summary>
    Blocked,
    /// <summary>Connection failed or remote returned an error after all retries.</summary>
    Unreachable,
    /// <summary>Timeout elapsed (per-attempt or total).</summary>
    TimedOut,
    /// <summary>Circuit breaker is open: the request was not even attempted.</summary>
    CircuitOpen,
}

public sealed record ProbeResult(ProbeStatus Status, string? ContentType = null, int? RetryAfterSeconds = null);

public interface IImageUrlProbe
{
    Task<ProbeResult> ProbeAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>
/// Checks that a remote URL serves an image using a HEAD request. The injected <see cref="HttpClient"/>
/// is wrapped by the Timeout + Retry + Circuit Breaker pipeline registered in
/// <see cref="ResilienceExtensions.AddRemoteImageProbe"/>; this class only translates the
/// outcomes/exceptions into a <see cref="ProbeResult"/>.
/// </summary>
public sealed class ImageUrlProbe(HttpClient http, RemoteProbeOptions options) : IImageUrlProbe
{
    public async Task<ProbeResult> ProbeAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
            return new ProbeResult(ProbeStatus.InvalidUrl);

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && !RemoteUrlGuard.IsPublicAddress(literal))
            return new ProbeResult(ProbeStatus.Blocked);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return new ProbeResult(ProbeStatus.Unreachable);

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? new ProbeResult(ProbeStatus.Image, contentType)
                : new ProbeResult(ProbeStatus.NotImage, contentType);
        }
        catch (BrokenCircuitException)
        {
            return new ProbeResult(ProbeStatus.CircuitOpen, RetryAfterSeconds: (int)Math.Ceiling(options.BreakDurationMs / 1000.0));
        }
        catch (TimeoutRejectedException)
        {
            return new ProbeResult(ProbeStatus.TimedOut);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the caller went away - not a dependency failure
        }
        catch (TaskCanceledException)
        {
            return new ProbeResult(ProbeStatus.TimedOut); // HttpClient.Timeout safety net
        }
        catch (HttpRequestException ex)
        {
            return new ProbeResult(RemoteUrlGuard.IsBlocked(ex) ? ProbeStatus.Blocked : ProbeStatus.Unreachable);
        }
    }
}
