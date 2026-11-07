using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NexusCore.Api.Extensions;
using NexusCore.Api.Services;

namespace NexusCore.Api.Hubs;

public sealed class CloudStreamHub(
    CloudServerService cloudServers,
    DbService db,
    CloudDiagnosticsLog diagnostics,
    StreamPrivacyService privacy,
    StreamTelemetryService streamTelemetry,
    SessionTelemetryService sessionTelemetry) : Hub
{
    private const string PlayerSessionKey = "player_session_id";
    private const string AgentSessionKey = "agent_session_id";
    private const string AgentServerKey = "agent_server_id";

    public override Task OnConnectedAsync()
    {
        var userId = Context.User?.Identity?.IsAuthenticated == true
            ? Context.User.GetUserId().ToString()
            : "anonymous/agent";
        diagnostics.Info("stream-hub", "Client connected", $"conn={Context.ConnectionId} user={userId}");
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var detail = $"conn={Context.ConnectionId}";
        if (Context.Items.TryGetValue(PlayerSessionKey, out var player) && player is int playerSession)
            detail += $" role=player session={playerSession}";
        if (Context.Items.TryGetValue(AgentSessionKey, out var agent) && agent is int agentSession)
        {
            detail += $" role=agent session={agentSession}";
            await sessionTelemetry.AgentDisconnectedAsync(agentSession, Context.ConnectionId);
        }

        if (exception is not null)
            diagnostics.Warn("stream-hub", "Connection dropped", $"{detail} error={exception.Message}");
        else
            diagnostics.Info("stream-hub", "Client disconnected", detail);

        await base.OnDisconnectedAsync(exception);
    }

    public Task<long> Ping() => Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    [Authorize]
    public async Task PlayerJoin(int sessionId)
    {
        var userId = Context.User!.GetUserId();
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var active = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM cloud_sessions WHERE session_id=@sessionId AND user_id=@userId AND status='active'",
            new { sessionId, userId });
        if (active is null)
            throw new HubException("No active session");

        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Player(sessionId));
        Context.Items[PlayerSessionKey] = sessionId;
        diagnostics.Info("stream-hub", "Player joined stream", $"session={sessionId} user={userId} conn={Context.ConnectionId}");
        await Clients.Caller.SendAsync("JoinedStream", sessionId);
        var snapshot = sessionTelemetry.Get(sessionId);
        if (snapshot is not null)
            await Clients.Caller.SendAsync(SessionTelemetryService.TelemetryEvent, snapshot);
    }

    [Authorize]
    public async Task SpectatorJoin(int sessionId)
    {
        var userId = Context.User!.GetUserId();
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var session = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT cs.user_id, cs.allow_spectators, cs.is_real_stream,
                   g.name AS game_name, g.cover_url,
                   s.name AS server_name, s.region AS server_region
            FROM cloud_sessions cs
            JOIN games g ON g.game_id=cs.game_id
            LEFT JOIN cloud_servers s ON s.server_id=cs.server_id
            WHERE cs.session_id=@sessionId AND cs.status='active'
            """,
            new { sessionId });
        if (session is null)
            throw new HubException("Session not found");
        if ((int)session.user_id == userId)
            throw new HubException("Use PlayerJoin for your own session");

        var allowed = Convert.ToBoolean(session.allow_spectators);
        privacy.SetCached(sessionId, allowed);
        if (allowed)
            await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Spectator(sessionId));

        var queueEntry = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT status, joined_at FROM cloud_queue WHERE user_id=@userId",
            new { userId });
        int? queuePosition = queueEntry is null
            ? null
            : (string)queueEntry.status == "ready"
                ? 0
                : await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*)+1 FROM cloud_queue WHERE status='waiting' AND joined_at < @joinedAt",
                new { joinedAt = queueEntry.joined_at });

        await Clients.Caller.SendAsync("WatchState", new
        {
            session_id = sessionId,
            allow_spectators = allowed,
            can_view = allowed,
            is_real_stream = Convert.ToBoolean(session.is_real_stream),
            server_name = session.server_name as string,
            server_region = session.server_region as string,
            game_name = allowed ? session.game_name as string : null,
            cover_url = allowed ? session.cover_url as string : null,
            queue_position = queuePosition,
            estimated_wait_mins = queuePosition * 4,
            in_queue = queueEntry is not null
        });

        if (allowed && Convert.ToBoolean(session.is_real_stream))
            await Clients.Caller.SendAsync("StreamReady");
    }

    [Authorize]
    public async Task SendInput(int sessionId, StreamInput input)
    {
        var userId = Context.User!.GetUserId();
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var active = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM cloud_sessions WHERE session_id=@sessionId AND user_id=@userId AND status='active' AND is_real_stream=TRUE",
            new { sessionId, userId });
        if (active is null)
            throw new HubException("No active stream session");
        await Clients.Group(StreamGroups.Agent(sessionId)).SendAsync("ReceiveInput", input);
    }

    public async Task AgentJoin(int sessionId, int serverId, string? password)
    {
        if (!await cloudServers.VerifyAccessAsync(serverId, password))
            throw new HubException("Invalid agent credentials");

        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var active = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM cloud_sessions WHERE session_id=@sessionId AND server_id=@serverId AND status='active' AND is_real_stream=TRUE",
            new { sessionId, serverId });
        if (active is null)
            throw new HubException("Session not found");

        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Agent(sessionId));
        Context.Items[AgentSessionKey] = sessionId;
        Context.Items[AgentServerKey] = serverId;
        diagnostics.Info("stream-hub", "Agent joined stream", $"session={sessionId} server={serverId} conn={Context.ConnectionId}");
        await sessionTelemetry.AgentConnectedAsync(sessionId, Context.ConnectionId);
        await Clients.Group(StreamGroups.Player(sessionId)).SendAsync("StreamReady");
        await Clients.Caller.SendAsync("AgentJoined", sessionId);
    }

    public async Task ReportPlayerTelemetry(int sessionId, PlayerStreamMetrics? metrics)
    {
        if (metrics is null || !IsValid(metrics))
            throw new HubException("Invalid stream telemetry");

        var userId = Context.User?.GetUserId();
        if (userId is null)
            throw new HubException("Authentication required");

        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var active = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM cloud_sessions WHERE session_id=@sessionId AND user_id=@userId AND status='active' AND is_real_stream=TRUE",
            new { sessionId, userId });
        if (active is null)
            throw new HubException("No active stream session");
        streamTelemetry.RecordPlayer(sessionId, metrics);
    }

    public async Task ReportAgentTelemetry(
        int sessionId,
        int serverId,
        string? password,
        AgentStreamMetrics? metrics)
    {
        if (metrics is null || !IsValid(metrics))
            throw new HubException("Invalid stream telemetry");
        await ValidateAgentAsync(sessionId, serverId, password);
        streamTelemetry.RecordAgent(sessionId, metrics);
    }

    public async Task SendFrame(int sessionId, int serverId, string? password, string frameBase64)
    {
        await ValidateAgentAsync(sessionId, serverId, password);
        if (string.IsNullOrEmpty(frameBase64))
            throw new HubException("Frame is required");

        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Clients.Group(StreamGroups.Player(sessionId)).SendAsync("ReceiveFrame", frameBase64);
            if (await privacy.GetAllowSpectatorsAsync(sessionId))
                await Clients.Group(StreamGroups.Spectator(sessionId)).SendAsync("ReceiveFrame", frameBase64);
        }
        finally
        {
            streamTelemetry.RecordRelay(sessionId, timer.Elapsed.TotalMilliseconds, frameBase64.Length);
        }
    }

    private async Task ValidateAgentAsync(int sessionId, int serverId, string? password)
    {
        if (!Context.Items.TryGetValue(AgentSessionKey, out var registeredSession)
            || registeredSession is not int joinedSession
            || joinedSession != sessionId
            || !Context.Items.TryGetValue(AgentServerKey, out var registeredServer)
            || registeredServer is not int joinedServer
            || joinedServer != serverId
            || !await cloudServers.VerifyAccessAsync(serverId, password))
            throw new HubException("Agent is not authorized for this session");
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
        && metrics.FramesReceived >= 0
        && metrics.FramesReceived <= 10_000
        && metrics.FramesDisplayed >= 0
        && metrics.FramesDisplayed <= metrics.FramesReceived;

    private static bool IsInRange(double value, double min, double max) =>
        double.IsFinite(value) && value >= min && value <= max;

    public sealed record StreamInput(string Type, int? X, int? Y, string? Button, string? Key, bool? Down, int? Vk);
}
