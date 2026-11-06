namespace NexusCore.Api.Services;

public sealed class StreamTelemetryService
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly Dictionary<(int SessionId, string Source), StreamTelemetrySnapshot> _snapshots = [];
    private readonly Dictionary<int, RelayWindow> _relayWindows = [];
    private DateTime _lastPrunedAt = DateTime.MinValue;

    public void RecordAgent(int sessionId, AgentStreamMetrics metrics) =>
        Record(sessionId, "agent", metrics);

    public void RecordPlayer(int sessionId, PlayerStreamMetrics metrics) =>
        Record(sessionId, "player", metrics);

    public void RecordRelay(int sessionId, double elapsedMs, int frameBytes)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            PruneExpired(now);
            if (!_relayWindows.TryGetValue(sessionId, out var window))
            {
                window = new RelayWindow(now);
                _relayWindows.Add(sessionId, window);
            }

            window.Frames++;
            window.Bytes += frameBytes;
            window.TotalMs += elapsedMs;
            window.MaxMs = Math.Max(window.MaxMs, elapsedMs);

            var durationMs = (now - window.StartedAt).TotalMilliseconds;
            if (durationMs < 10_000) return;

            _snapshots[(sessionId, "hub")] = new StreamTelemetrySnapshot(
                sessionId,
                "hub",
                now,
                new HubStreamMetrics(
                    window.Frames,
                    window.Bytes,
                    durationMs,
                    window.TotalMs / window.Frames,
                    window.MaxMs));
            _relayWindows[sessionId] = new RelayWindow(now);
        }
    }

    public IReadOnlyList<StreamTelemetrySnapshot> Recent(int limit = 120)
    {
        lock (_gate)
        {
            PruneExpired(DateTime.UtcNow);
            return _snapshots.Values
                .OrderByDescending(snapshot => snapshot.At)
                .Take(limit)
                .ToArray();
        }
    }

    private void Record(int sessionId, string source, object metrics)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            PruneExpired(now);
            _snapshots[(sessionId, source)] = new StreamTelemetrySnapshot(sessionId, source, now, metrics);
        }
    }

    private void PruneExpired(DateTime now)
    {
        if (now - _lastPrunedAt < TimeSpan.FromSeconds(30)) return;
        _lastPrunedAt = now;
        var expiredBefore = now - Retention;
        foreach (var key in _snapshots
                     .Where(pair => pair.Value.At < expiredBefore)
                     .Select(pair => pair.Key)
                     .ToArray())
            _snapshots.Remove(key);

        foreach (var sessionId in _relayWindows
                     .Where(pair => pair.Value.StartedAt < expiredBefore)
                     .Select(pair => pair.Key)
                     .ToArray())
            _relayWindows.Remove(sessionId);
    }

    private sealed class RelayWindow(DateTime startedAt)
    {
        public DateTime StartedAt { get; } = startedAt;
        public int Frames { get; set; }
        public long Bytes { get; set; }
        public double TotalMs { get; set; }
        public double MaxMs { get; set; }
    }
}

public sealed record StreamTelemetrySnapshot(int SessionId, string Source, DateTime At, object Metrics);

public sealed record AgentStreamMetrics(
    double CaptureMsAvg,
    double CaptureMsMax,
    double HubRoundTripMsAvg,
    double HubRoundTripMsMax,
    double FrameBytesAvg,
    double FrameBytesMax,
    double TargetFps,
    double Quality,
    int FramesSent,
    int FramesSkipped,
    int Failures);

public sealed record PlayerStreamMetrics(
    double RenderFps,
    double FrameIntervalMsAvg,
    double FrameIntervalJitterMs,
    double ReceiveToDisplayMsAvg,
    double ReceiveToDisplayMsMax,
    double HubRoundTripMsAvg,
    double HubRoundTripMsMax,
    double InputHubRoundTripMsAvg,
    double InputHubRoundTripMsMax,
    int FramesReceived,
    int FramesDisplayed);

public sealed record HubStreamMetrics(
    int FramesRelayed,
    long Base64BytesRelayed,
    double WindowMs,
    double RelayMsAvg,
    double RelayMsMax);
