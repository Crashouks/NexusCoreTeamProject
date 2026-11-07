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
    SessionTelemetryService sessionTelemetry,
    StreamTelemetryService streamTelemetry) : Hub
{
    private const string PlayerSessionKey = "player_session_id";
    private const string AgentSessionKey = "agent_session_id";
    private const string AgentServerKey = "agent_server_id";
    private const int MaxFrameBase64Length = 1_300_000;

    public override Task OnConnectedAsync()
    {
        var userId = Context.User?.Identity?.IsAuthenticated == true
            ? Context.User.GetUserId().ToString()
            : "agent";
        diagnostics.Info("stream-hub", "Client connected", $"conn={Context.ConnectionId} user={userId}");
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(AgentSessionKey, out var agentSession) && agentSession is int sessionId)
        {
            await sessionTelemetry.AgentDisconnectedAsync(sessionId, Context.ConnectionId);
            diagnostics.Info("stream-hub", "Agent disconnected", $"conn={Context.ConnectionId} session={sessionId}");
        }
        else
        {
            diagnostics.Info("stream-hub", "Client disconnected",
                $"conn={Context.ConnectionId}{(exception is null ? string.Empty : $" error={exception.Message}")}");
        }

        await base.OnDisconnectedAsync(exception);
    }

    public Task<long> Ping() => Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    [Authorize]
    public async Task PlayerJoin(int sessionId)
    {
        var userId = Context.User!.GetUserId();
        await using var connection = db.CreateConnection();
        await connection.OpenAsync();
        var active = await connection.ExecuteScalarAsync<int?>(
            """
            SELECT 1 FROM cloud_sessions
            WHERE session_id=@sessionId AND user_id=@userId
              AND status='active' AND is_real_stream=TRUE
            """,
            new { sessionId, userId });
        if (active is null)
            throw new HubException("No active stream session");

        if (Context.Items.TryGetValue(PlayerSessionKey, out var current) && current is int oldSession && oldSession != sessionId)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, StreamGroups.Player(oldSession));

        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Player(sessionId));
        Context.Items[PlayerSessionKey] = sessionId;
        await Clients.Caller.SendAsync("JoinedStream", sessionId);
        var snapshot = sessionTelemetry.Get(sessionId);
        if (snapshot is not null)
            await Clients.Caller.SendAsync(SessionTelemetryService.TelemetryEvent, snapshot);
    }

    public async Task AgentJoin(int sessionId, int serverId, string? password)
    {
        if (sessionId <= 0 || serverId <= 0 || !await cloudServers.VerifyAccessAsync(serverId, password))
            throw new HubException("Invalid agent credentials");

        await using var connection = db.CreateConnection();
        await connection.OpenAsync();
        var active = await connection.ExecuteScalarAsync<int?>(
            """
            SELECT 1 FROM cloud_sessions
            WHERE session_id=@sessionId AND server_id=@serverId
              AND status='active' AND is_real_stream=TRUE
            """,
            new { sessionId, serverId });
        if (active is null)
            throw new HubException("Session is not active on this server");

        if (Context.Items.TryGetValue(AgentSessionKey, out var currentSession)
            && currentSession is int oldSession
            && Context.Items.TryGetValue(AgentServerKey, out var currentServer)
            && currentServer is int oldServer)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, StreamGroups.Agent(oldSession));
            await sessionTelemetry.AgentDisconnectedAsync(oldSession, Context.ConnectionId);
            if (oldServer != serverId && oldSession == sessionId)
                throw new HubException("Connection is already bound to another server");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Agent(sessionId));
        Context.Items[AgentSessionKey] = sessionId;
        Context.Items[AgentServerKey] = serverId;
        await sessionTelemetry.AgentConnectedAsync(sessionId, Context.ConnectionId);
        await Clients.Caller.SendAsync("AgentJoined", sessionId);
    }

    [Authorize]
    public async Task SpectatorJoin(int sessionId)
    {
        var userId = Context.User!.GetUserId();
        await using var connection = db.CreateConnection();
        await connection.OpenAsync();
        var state = await connection.QuerySingleOrDefaultAsync<SpectatorSession>(
            """
            SELECT user_id AS UserId, allow_spectators AS AllowSpectators
            FROM cloud_sessions
            WHERE session_id=@sessionId AND status='active' AND is_real_stream=TRUE
            """,
            new { sessionId });

        var canView = state is not null
            && state.UserId != userId
            && state.AllowSpectators;
        if (!canView)
            throw new HubException("This stream is not available to spectators");

        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Spectator(sessionId));
        await Clients.Caller.SendAsync("WatchState", new { can_view = true, is_real_stream = true });
    }

    [Authorize]
    public async Task SendInput(int sessionId, StreamInput? input)
    {
        if (input is null || !input.IsValid())
            throw new HubException("Invalid stream input");
        if (!Context.Items.TryGetValue(PlayerSessionKey, out var registered)
            || registered is not int registeredSession
            || registeredSession != sessionId)
            throw new HubException("Player has not joined this session");

        var userId = Context.User!.GetUserId();
        await using var connection = db.CreateConnection();
        await connection.OpenAsync();
        var active = await connection.ExecuteScalarAsync<int?>(
            """
            SELECT 1 FROM cloud_sessions
            WHERE session_id=@sessionId AND user_id=@userId
              AND status='active' AND is_real_stream=TRUE
            """,
            new { sessionId, userId });
        if (active is null)
            throw new HubException("No active stream session");

        await Clients.Group(StreamGroups.Agent(sessionId)).SendAsync("ReceiveInput", input);
    }

    public async Task ReportAgentTelemetry(
        int sessionId, int serverId, string? password, AgentStreamMetrics? metrics)
    {
        EnsureAgentConnection(sessionId, serverId);
        if (metrics is null || !IsValid(metrics))
            throw new HubException("Invalid stream telemetry");

        streamTelemetry.RecordAgent(sessionId, metrics);
    }

    public async Task ReportTelemetry(int sessionId, int latencyMs, int fps, int quality)
    {
        EnsureAgentConnection(sessionId);
        if (latencyMs is < 0 or > 60_000 || fps is < 0 or > 240 || quality is < 0 or > 100)
            throw new HubException("Invalid stream telemetry");

        await sessionTelemetry.ReportAgentSampleAsync(sessionId, latencyMs, fps, quality);
    }

    [Authorize]
    public async Task ReportPlayerTelemetry(int sessionId, PlayerStreamMetrics? metrics)
    {
        if (metrics is null || !IsValid(metrics))
            throw new HubException("Invalid stream telemetry");
        if (!Context.Items.TryGetValue(PlayerSessionKey, out var registered)
            || registered is not int registeredSession
            || registeredSession != sessionId)
            throw new HubException("Player has not joined this session");

        var userId = Context.User!.GetUserId();
        await using var connection = db.CreateConnection();
        await connection.OpenAsync();
        var active = await connection.ExecuteScalarAsync<int?>(
            """
            SELECT 1 FROM cloud_sessions
            WHERE session_id=@sessionId AND user_id=@userId
              AND status='active' AND is_real_stream=TRUE
            """,
            new { sessionId, userId });
        if (active is null)
            throw new HubException("No active stream session");

        streamTelemetry.RecordPlayer(sessionId, metrics);
    }

    public async Task SendFrame(int sessionId, int serverId, string? password, string? frameBase64)
    {
        EnsureAgentConnection(sessionId, serverId);
        if (string.IsNullOrEmpty(frameBase64) || frameBase64.Length > MaxFrameBase64Length)
            throw new HubException("Invalid stream frame");

        try
        {
            _ = Convert.FromBase64String(frameBase64);
        }
        catch (FormatException)
        {
            throw new HubException("Invalid stream frame");
        }

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

    private void EnsureAgentConnection(int sessionId, int? serverId = null)
    {
        if (!Context.Items.TryGetValue(AgentSessionKey, out var registeredSession)
            || registeredSession is not int boundSession
            || boundSession != sessionId
            || !Context.Items.TryGetValue(AgentServerKey, out var registeredServer)
            || registeredServer is not int boundServer
            || (serverId.HasValue && serverId.Value != boundServer))
            throw new HubException("Agent has not joined this session");
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
        && metrics.FramesReceived is >= 0 and <= 10_000
        && metrics.FramesDisplayed >= 0 && metrics.FramesDisplayed <= metrics.FramesReceived;

    private static bool IsInRange(double value, double minInclusive, double maxInclusive) =>
        double.IsFinite(value) && value >= minInclusive && value <= maxInclusive;

    private sealed record SpectatorSession(int UserId, bool AllowSpectators);
}

public sealed class StreamInput
{
    public string Type { get; init; } = "";
    public int? X { get; init; }
    public int? Y { get; init; }
    public string? Button { get; init; }
    public string? Code { get; init; }
    public string? Key { get; init; }
    public int? Vk { get; init; }

    public bool IsValid()
    {
        if (Type is "move" or "mousedown" or "mouseup")
        {
            if (X is < 0 or > 16_384 || Y is < 0 or > 16_384)
                return false;
            if (Type != "move" && Button is not ("left" or "middle" or "right"))
                return false;
            return X.HasValue && Y.HasValue;
        }

        return Type is "keydown" or "keyup"
            && Vk is > 0 and <= 255
            && Code is { Length: > 0 and <= 32 }
            && Key is { Length: > 0 and <= 32 };
    }
}
