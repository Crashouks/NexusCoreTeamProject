using Microsoft.AspNetCore.SignalR;
using NexusCore.Api.Hubs;

namespace NexusCore.Api.Services;

/// <summary>Lifecycle states of a real (agent-backed) cloud stream, as pushed to the player.</summary>
public static class StreamStatus
{
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Stopped = "stopped";

    /// <summary>Returns the canonical lowercase status, or null if <paramref name="value"/> is not a known status.</summary>
    public static string? Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Starting => Starting,
        Running => Running,
        Stopped => Stopped,
        _ => null
    };
}

/// <summary>
/// Point-in-time telemetry for one cloud session. Serialized by SignalR with the app-wide
/// snake_case policy (<c>session_id</c>, <c>agent_online</c>, <c>agent_latency_ms</c>, ...).
/// </summary>
public record SessionTelemetry(
    int SessionId,
    string Status,
    string? Reason,
    bool AgentOnline,
    int? AgentLatencyMs,
    int? Fps,
    int? Quality,
    DateTime UpdatedAt);

/// <summary>
/// Single source of truth for the live state of cloud streaming sessions. Keeps the latest
/// snapshot per session (so a player who joins or reconnects gets the current state immediately)
/// and pushes every change to that session's <see cref="StreamGroups.Player"/> group — which
/// <c>CloudStreamHub.PlayerJoin</c> only lets the session's owner join. Spectators are never
/// sent telemetry.
/// </summary>
public class SessionTelemetryService(IHubContext<CloudStreamHub> hub, CloudDiagnosticsLog diag)
{
    /// <summary>Event sent when the session status changes (Starting / Running / Stopped).</summary>
    public const string StatusChangedEvent = "SessionStatusChanged";

    /// <summary>Event sent with the full snapshot: on join, on latency updates, on agent (dis)connect.</summary>
    public const string TelemetryEvent = "SessionTelemetry";

    private static readonly TimeSpan StoppedRetention = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Dictionary<int, SessionTelemetry> _sessions = new();
    private readonly Dictionary<int, string> _agentConnections = new();

    public SessionTelemetry? Get(int sessionId)
    {
        lock (_gate)
            return _sessions.GetValueOrDefault(sessionId);
    }

    /// <summary>
    /// Moves a session to <paramref name="status"/> and pushes it to the owner. A stopped session
    /// stays stopped (late "running" reports from a dying agent are ignored), and repeating the
    /// current status is a no-op so the player is not spammed.
    /// </summary>
    public async Task SetStatusAsync(int sessionId, string status, string? reason = null)
    {
        var normalized = StreamStatus.Normalize(status);
        if (normalized == null) return;

        SessionTelemetry next;
        lock (_gate)
        {
            PruneLocked();
            if (_sessions.TryGetValue(sessionId, out var current))
            {
                if (current.Status == StreamStatus.Stopped && normalized != StreamStatus.Stopped) return;
                if (current.Status == normalized) return;
                next = current with { Status = normalized, Reason = reason, UpdatedAt = DateTime.UtcNow };
            }
            else
            {
                next = new SessionTelemetry(sessionId, normalized, reason, false, null, null, null, DateTime.UtcNow);
            }

            if (normalized == StreamStatus.Stopped)
            {
                next = next with { AgentOnline = false, AgentLatencyMs = null };
                _agentConnections.Remove(sessionId);
            }
            _sessions[sessionId] = next;
        }

        diag.Info("telemetry", $"Session status → {normalized}", $"session={sessionId}{(reason != null ? $" reason={reason}" : "")}");
        await hub.Clients.Group(StreamGroups.Player(sessionId)).SendAsync(StatusChangedEvent, next);
    }

    /// <summary>
    /// Records a periodic latency/fps/quality sample from the agent. A sample only exists while the
    /// agent is capturing, so it also implies "running" (this self-heals state lost on API restart).
    /// </summary>
    public async Task ReportAgentSampleAsync(int sessionId, int latencyMs, int fps, int quality)
    {
        var statusChanged = false;
        SessionTelemetry next;
        lock (_gate)
        {
            var current = _sessions.GetValueOrDefault(sessionId)
                ?? new SessionTelemetry(sessionId, StreamStatus.Starting, null, true, null, null, null, DateTime.UtcNow);
            if (current.Status == StreamStatus.Stopped) return;

            statusChanged = current.Status != StreamStatus.Running;
            next = current with
            {
                Status = StreamStatus.Running,
                Reason = null,
                AgentOnline = true,
                AgentLatencyMs = Math.Clamp(latencyMs, 0, 60_000),
                Fps = Math.Clamp(fps, 0, 1_000),
                Quality = Math.Clamp(quality, 0, 100),
                UpdatedAt = DateTime.UtcNow
            };
            _sessions[sessionId] = next;
        }

        if (statusChanged)
            await hub.Clients.Group(StreamGroups.Player(sessionId)).SendAsync(StatusChangedEvent, next);
        await hub.Clients.Group(StreamGroups.Player(sessionId)).SendAsync(TelemetryEvent, next);
    }

    /// <summary>Marks the agent's hub connection for a session as up (called from <c>AgentJoin</c>).</summary>
    public async Task AgentConnectedAsync(int sessionId, string connectionId)
    {
        SessionTelemetry next;
        lock (_gate)
        {
            _agentConnections[sessionId] = connectionId;
            var current = _sessions.GetValueOrDefault(sessionId)
                ?? new SessionTelemetry(sessionId, StreamStatus.Starting, null, true, null, null, null, DateTime.UtcNow);
            if (current.Status == StreamStatus.Stopped) return;
            next = current with { AgentOnline = true, UpdatedAt = DateTime.UtcNow };
            _sessions[sessionId] = next;
        }

        await hub.Clients.Group(StreamGroups.Player(sessionId)).SendAsync(TelemetryEvent, next);
    }

    /// <summary>
    /// Marks the agent as offline when its hub connection drops. Ignored if the agent has already
    /// reconnected on a newer connection (a stale disconnect must not flip a healthy agent offline).
    /// </summary>
    public async Task AgentDisconnectedAsync(int sessionId, string connectionId)
    {
        SessionTelemetry next;
        lock (_gate)
        {
            if (!_agentConnections.TryGetValue(sessionId, out var active) || active != connectionId) return;
            _agentConnections.Remove(sessionId);
            if (!_sessions.TryGetValue(sessionId, out var current) || current.Status == StreamStatus.Stopped) return;
            next = current with { AgentOnline = false, AgentLatencyMs = null, UpdatedAt = DateTime.UtcNow };
            _sessions[sessionId] = next;
        }

        diag.Warn("telemetry", "Agent connection lost", $"session={sessionId}");
        await hub.Clients.Group(StreamGroups.Player(sessionId)).SendAsync(TelemetryEvent, next);
    }

    private void PruneLocked()
    {
        var cutoff = DateTime.UtcNow - StoppedRetention;
        List<int>? stale = null;
        foreach (var (id, t) in _sessions)
        {
            if (t.Status == StreamStatus.Stopped && t.UpdatedAt < cutoff)
                (stale ??= []).Add(id);
        }
        if (stale == null) return;
        foreach (var id in stale) _sessions.Remove(id);
    }
}
