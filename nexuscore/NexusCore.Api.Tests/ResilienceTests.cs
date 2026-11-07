using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using NexusCore.Api.Resilience;

namespace NexusCore.Api.Tests;

/// <summary>
/// Acceptance: Timeout, Retry and Circuit Breaker are configured and tested for external requests.
/// The real pipeline from <see cref="ResilienceExtensions.AddRemoteImageProbe"/> is exercised;
/// only the network (primary HTTP handler) is replaced with a scripted stub.
/// </summary>
public class ResilienceTests
{
    private const string Url = "https://images.example.com/cover.png";

    private sealed class StubHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> script) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            script(Interlocked.Increment(ref _calls), ct);
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private static HttpResponseMessage Png()
    {
        var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Fast defaults; individual tests override what they exercise. Breaker is effectively off unless asked for.</summary>
    private static RemoteProbeOptions Options(Action<RemoteProbeOptions>? tweak = null)
    {
        var o = new RemoteProbeOptions
        {
            TotalTimeoutMs = 5_000,
            AttemptTimeoutMs = 300,
            MaxRetries = 2,
            RetryDelayMs = 5,
            BreakerMinimumThroughput = 1_000,
            BreakerSamplingMs = 60_000,
            BreakDurationMs = 1_000,
        };
        tweak?.Invoke(o);
        return o;
    }

    private static IImageUrlProbe Probe(RemoteProbeOptions options, StubHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRemoteImageProbe(options).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider().GetRequiredService<IImageUrlProbe>();
    }

    // ---------------------------------------------------------------- Timeout

    [Fact]
    public async Task Timeout_HangingHost_IsCutOffAfterAttemptTimeout()
    {
        var handler = new StubHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Status(HttpStatusCode.OK); });
        var probe = Probe(Options(o => { o.MaxRetries = 0; o.AttemptTimeoutMs = 200; }), handler);

        var sw = Stopwatch.StartNew();
        var result = await probe.ProbeAsync(Url);
        sw.Stop();

        Assert.Equal(ProbeStatus.TimedOut, result.Status);
        Assert.Equal(1, handler.Calls);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Timeout_TotalBudget_CapsTheWholeCall_IncludingRetries()
    {
        var handler = new StubHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Status(HttpStatusCode.OK); });
        var probe = Probe(Options(o => { o.MaxRetries = 10; o.AttemptTimeoutMs = 200; o.RetryDelayMs = 50; o.TotalTimeoutMs = 700; }), handler);

        var sw = Stopwatch.StartNew();
        var result = await probe.ProbeAsync(Url);
        sw.Stop();

        Assert.Equal(ProbeStatus.TimedOut, result.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        Assert.True(handler.Calls < 11, "total timeout should stop the retry loop early");
    }

    // ---------------------------------------------------------------- Retry

    [Fact]
    public async Task Retry_TransientFailure_ThenRecovery_Succeeds()
    {
        var handler = new StubHandler((n, _) => Task.FromResult(n <= 2 ? Status(HttpStatusCode.ServiceUnavailable) : Png()));
        var probe = Probe(Options(), handler);

        var result = await probe.ProbeAsync(Url);

        Assert.Equal(ProbeStatus.Image, result.Status);
        Assert.Equal(3, handler.Calls); // 1 attempt + 2 retries
    }

    [Fact]
    public async Task Retry_NetworkException_IsRetried()
    {
        var handler = new StubHandler((n, _) => n == 1
            ? throw new HttpRequestException("connection reset")
            : Task.FromResult(Png()));

        Assert.Equal(ProbeStatus.Image, (await Probe(Options(), handler).ProbeAsync(Url)).Status);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Retry_AllAttemptsFail_GivesUpAfterConfiguredLimit()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Status(HttpStatusCode.BadGateway)));
        var probe = Probe(Options(o => o.MaxRetries = 2), handler);

        var result = await probe.ProbeAsync(Url);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
        Assert.Equal(3, handler.Calls); // never more than 1 + MaxRetries
    }

    [Fact]
    public async Task Retry_ClientError404_IsNotRetried()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Status(HttpStatusCode.NotFound)));

        var result = await Probe(Options(), handler).ProbeAsync(Url);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Retry_WaitsBetweenAttempts_InsteadOfHammeringTheHost()
    {
        var stamps = new List<long>();
        var handler = new StubHandler((_, _) =>
        {
            lock (stamps) stamps.Add(Stopwatch.GetTimestamp());
            return Task.FromResult(Status(HttpStatusCode.ServiceUnavailable));
        });
        var probe = Probe(Options(o => { o.MaxRetries = 2; o.RetryDelayMs = 150; }), handler);

        await probe.ProbeAsync(Url);

        Assert.Equal(3, stamps.Count);
        var gap1 = Stopwatch.GetElapsedTime(stamps[0], stamps[1]);
        var gap2 = Stopwatch.GetElapsedTime(stamps[1], stamps[2]);
        // Exponential back-off with jitter: exact values are random, but both gaps must be clearly non-zero.
        Assert.True(gap1 >= TimeSpan.FromMilliseconds(50), $"first back-off too short: {gap1}");
        Assert.True(gap2 >= TimeSpan.FromMilliseconds(50), $"second back-off too short: {gap2}");
    }

    // ---------------------------------------------------------------- Circuit breaker

    private static RemoteProbeOptions BreakerOptions() => Options(o =>
    {
        o.MaxRetries = 0;
        o.BreakerMinimumThroughput = 3;
        o.BreakerFailureRatio = 0.5;
        o.BreakDurationMs = 1_000;
    });

    [Fact]
    public async Task CircuitBreaker_Opens_AfterRepeatedFailures_AndStopsCallingTheHost()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Status(HttpStatusCode.ServiceUnavailable)));
        var probe = Probe(BreakerOptions(), handler);

        for (var i = 0; i < 3; i++)
            Assert.Equal(ProbeStatus.Unreachable, (await probe.ProbeAsync(Url)).Status); // CLOSED: calls go through
        Assert.Equal(3, handler.Calls);

        var blocked = await probe.ProbeAsync(Url);                                       // OPEN: rejected immediately

        Assert.Equal(ProbeStatus.CircuitOpen, blocked.Status);
        Assert.True(blocked.RetryAfterSeconds >= 1);
        Assert.Equal(3, handler.Calls); // the failing dependency was NOT contacted
    }

    [Fact]
    public async Task CircuitBreaker_HalfOpen_ThenRecovers_WhenDependencyIsHealthyAgain()
    {
        var healthy = false;
        var handler = new StubHandler((_, _) => Task.FromResult(healthy ? Png() : Status(HttpStatusCode.ServiceUnavailable)));
        var probe = Probe(BreakerOptions(), handler);

        for (var i = 0; i < 3; i++) await probe.ProbeAsync(Url);
        Assert.Equal(ProbeStatus.CircuitOpen, (await probe.ProbeAsync(Url)).Status);

        healthy = true;
        await Task.Delay(1_300); // > BreakDuration -> next call is the half-open trial

        Assert.Equal(ProbeStatus.Image, (await probe.ProbeAsync(Url)).Status);  // trial succeeds -> CLOSED
        Assert.Equal(ProbeStatus.Image, (await probe.ProbeAsync(Url)).Status);  // normal traffic again
    }

    [Fact]
    public async Task CircuitBreaker_HalfOpenTrialFails_ReopensTheCircuit()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Status(HttpStatusCode.ServiceUnavailable)));
        var probe = Probe(BreakerOptions(), handler);

        for (var i = 0; i < 3; i++) await probe.ProbeAsync(Url);
        await Task.Delay(1_300);

        Assert.Equal(ProbeStatus.Unreachable, (await probe.ProbeAsync(Url)).Status);  // trial hits the host and fails
        Assert.Equal(ProbeStatus.CircuitOpen, (await probe.ProbeAsync(Url)).Status);  // open again
    }

    [Fact]
    public async Task CircuitBreaker_IgnoresClientErrors_404sDoNotOpenIt()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Status(HttpStatusCode.NotFound)));
        var probe = Probe(BreakerOptions(), handler);

        for (var i = 0; i < 10; i++)
            Assert.Equal(ProbeStatus.Unreachable, (await probe.ProbeAsync(Url)).Status);
        Assert.Equal(10, handler.Calls);
    }

    [Fact]
    public async Task CircuitBreaker_TimeoutsCountAsFailures()
    {
        var handler = new StubHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Png(); });
        var probe = Probe(Options(o => { o.MaxRetries = 0; o.AttemptTimeoutMs = 100; o.BreakerMinimumThroughput = 3; }), handler);

        for (var i = 0; i < 3; i++)
            Assert.Equal(ProbeStatus.TimedOut, (await probe.ProbeAsync(Url)).Status);
        Assert.Equal(ProbeStatus.CircuitOpen, (await probe.ProbeAsync(Url)).Status);
    }

    // ---------------------------------------------------------------- SSRF guard (so the breaker can't be tripped / abused)

    [Theory]
    [InlineData("http://127.0.0.1/a.png")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/x.png")]
    [InlineData("http://192.168.1.1/x.png")]
    [InlineData("http://172.16.0.1/x.png")]
    [InlineData("http://[::1]/x.png")]
    public async Task Ssrf_PrivateAndMetadataAddresses_AreBlocked(string url)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Png()));
        var result = await Probe(Options(), handler).ProbeAsync(url);
        Assert.Equal(ProbeStatus.Blocked, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/a.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData("https://user:secret@example.com/a.png")]
    public async Task Ssrf_NonHttpOrCredentialUrls_AreInvalid(string url)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Png()));
        Assert.Equal(ProbeStatus.InvalidUrl, (await Probe(Options(), handler).ProbeAsync(url)).Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Ssrf_BlockedDestinationAtConnectTime_IsNotRetried_AndNeverOpensTheCircuit()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connect failed", new BlockedDestinationException("blocked")));
        var probe = Probe(Options(o => { o.MaxRetries = 2; o.BreakerMinimumThroughput = 3; }), handler);

        for (var i = 0; i < 10; i++)
            Assert.Equal(ProbeStatus.Blocked, (await probe.ProbeAsync(Url)).Status);

        Assert.Equal(10, handler.Calls); // 1 call each: no retries, circuit never opened
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.0.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void IsPublicAddress_ClassifiesCorrectly(string ip, bool expected) =>
        Assert.Equal(expected, RemoteUrlGuard.IsPublicAddress(IPAddress.Parse(ip)));
}
