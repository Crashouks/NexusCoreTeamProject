using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NexusCore.Api.Extensions;
using NexusCore.Api.Services;
using System.Security.Claims;

namespace NexusCore.Api.Hubs;

/// <summary>
/// Minimal cloud stream hub implementation used by the API and the tests.
/// </summary>
public class CloudStreamHub(
    CloudServerService cloudServers,
    DbService db,
    CloudDiagnosticsLog diag,
    StreamPrivacyService privacy,
    SessionTelemetryService telemetry) : Hub
{
    private const string PlayerSessionKey = "player_session_id";
    private const string AgentSessionKey = "agent_session_id";
    private const string AgentServerKey = "agent_server_id";

    public override Task OnConnectedAsync()
    {
        var userId = Context.User?.Identity?.IsAuthenticated == true
            ? Context.User.GetUserId().ToString()
            : "anonymous";
        diag.Info("stream-hub", "Client connected", $"conn={Context.ConnectionId} user={userId}");
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var role = "client";
        int? sessionId = null;

        if (Context.Items.TryGetValue(PlayerSessionKey, out var player) && player is int playerSession)
        {
            role = "player";
            sessionId = playerSession;
        }
        else if (Context.Items.TryGetValue(AgentSessionKey, out var agent) && agent is int agentSession)
        {
            role = "agent";
            sessionId = agentSession;
        }

        var detail = $"conn={Context.ConnectionId} role={role}{(sessionId != null ? $" session={sessionId}" : string.Empty)}";
        if (exception != null)
            diag.Warn("stream-hub", "Connection dropped", $"{detail} error={exception.Message}");
        else
            diag.Info("stream-hub", "Client disconnected", detail);

        if (role == "agent" && sessionId != null)
            telemetry.RecordRelay(sessionId.Value, 0, 0);

        await base.OnDisconnectedAsync(exception);
    }

    public Task<long> Ping() => Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    [Authorize]
    public async Task PlayerJoin(int sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Player(sessionId));
        Context.Items[PlayerSessionKey] = sessionId;
        diag.Info("stream-hub", "Player joined stream", $"session={sessionId} user={Context.User?.GetUserId()} conn={Context.ConnectionId}");
        await Clients.Caller.SendAsync("JoinedStream", sessionId);
    }

    [Authorize]
    public async Task AgentJoin(int sessionId, int serverId, string? password)
    {
        if (!await cloudServers.VerifyAccessAsync(serverId, password))
            throw new HubException("Invalid agent credentials");

        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Agent(sessionId));
        Context.Items[AgentSessionKey] = sessionId;
        diag.Info("stream-hub", "Agent joined stream", $"session={sessionId} server={serverId} conn={Context.ConnectionId}");
        await Clients.Caller.SendAsync("AgentJoined", sessionId);
    }

    [Authorize]
    public async Task StreamPing(int sessionId)
    {
        var userId = Context.User!.GetUserId();
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();

        var active = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM cloud_sessions WHERE session_id=@sid AND user_id=@uid AND status='active' AND is_real_stream=TRUE",
            new { sid = sessionId, uid = userId });

        if (active == null)
            throw new HubException("No active stream session");
    }

    [Authorize]
    public async Task ReportPlayerTelemetry(int sessionId, PlayerStreamMetrics? metrics)
    {
        if (metrics is null || !IsValid(metrics))
            throw new HubException("Invalid stream telemetry");

        var userId = Context.User!.GetUserId();
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();

        var active = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM cloud_sessions WHERE session_id=@sid AND user_id=@uid AND status='active' AND is_real_stream=TRUE",
            new { sid = sessionId, uid = userId });

        if (active == null)
            throw new HubException("No active stream session");

        telemetry.RecordPlayer(sessionId, metrics);
    }

    [Authorize]
    public async Task ReportAgentTelemetry(int sessionId, int serverId, string? password, AgentStreamMetrics? metrics)
    {
        if (metrics is null || !IsValid(metrics))
            throw new HubException("Invalid stream telemetry");

        if (!await cloudServers.VerifyAccessAsync(serverId, password))
            throw new HubException("Invalid agent credentials");

        telemetry.RecordAgent(sessionId, metrics);
    }

    [Authorize]
    public async Task SendFrame(int sessionId, string frameBase64)
    {
        if (!Context.Items.TryGetValue(AgentSessionKey, out var registered) || registered is not int registeredId || registeredId != sessionId)
            throw new HubException("Agent has not joined this session");

        await telemetry.ReportAgentSampleAsync(sessionId, latencyMs, fps, quality);
    }

    private static bool IsValid(AgentStreamMetrics metrics) =>
        IsInRange(metrics.CaptureMsAvg, 0, 60_000)
        && IsInRange(metrics.CaptureMsMax, metrics.CaptureMsAvg, 60_000)
        && IsInRange(metrics.HubRoundTripMsAvg, 0, 60_000)
        && IsInRange(metrics.HubRoundTripMsMax, metrics.HubRoundTripMsAvg, 60_000)
        && IsInRange(metrics.FrameBytesAvg, 0, 1_000_000)
        && IsInRange(metrics.FrameBytesMax, metrics.FrameBytesAvg, 1_000_000)
        && IsInRange(metrics.TargetFps, 0, 240)
        && IsInRange(metrics.Quality, 0, 100)
        && metrics.FramesSent is >= 0 and <= 10_000
        && metrics.FramesSkipped is >= 0 and <= 10_000
        && metrics.Failures is >= 0 and <= 10_000;

    private static bool IsValid(PlayerStreamMetrics metrics) =>
        IsInRange(metrics.RenderFps, 0, 240)
        && IsInRange(metrics.FrameIntervalMsAvg, 0, 60_000)
        && IsInRange(metrics.FrameIntervalJitterMs, 0, 60_000)
        && IsInRange(metrics.ReceiveToDisplayMsAvg, 0, 60_000)
        && IsInRange(metrics.ReceiveToDisplayMsMax, metrics.ReceiveToDisplayMsAvg, 60_000)
        && IsInRange(metrics.HubRoundTripMsAvg, 0, 60_000)
        && IsInRange(metrics.HubRoundTripMsMax, metrics.HubRoundTripMsAvg, 60_000)
        && IsInRange(metrics.InputHubRoundTripMsAvg, 0, 60_000)
        && IsInRange(metrics.InputHubRoundTripMsMax, metrics.InputHubRoundTripMsAvg, 60_000)
        && metrics.FramesReceived >= 0 && metrics.FramesReceived <= 10_000
        && metrics.FramesDisplayed >= 0 && metrics.FramesDisplayed <= metrics.FramesReceived;

    private static bool IsInRange(double value, double minInclusive, double maxInclusive) =>
        double.IsFinite(value) && value >= minInclusive && value <= maxInclusive;
}
