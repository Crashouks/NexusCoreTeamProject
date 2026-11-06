using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace NexusCore.Api.Hubs;

/// <summary>
/// Real-time channel between the web client (player/spectator), the API and the Windows streaming agent.
/// <list type="bullet">
/// <item>Players authenticate with the normal JWT (cookie, or <c>access_token</c> query for WebSockets) and may
/// only join the group of a session they own — that group is the only audience for that session's telemetry.</item>
/// <item>The agent authenticates with its server id + agent password on <c>AgentJoin</c>; after that the
/// connection is bound to a single session and may push telemetry for it without re-sending the password.</item>
/// </list>
/// </summary>
public class CloudStreamHub(
    CloudServerService cloudServers,
    DbService db,
    CloudDiagnosticsLog diag,
    StreamPrivacyService privacy,
    StreamTelemetryService telemetry) : Hub
{
    private const string PlayerSessionKey = "player_session_id";
    private const string AgentSessionKey = "agent_session_id";

    public override Task OnConnectedAsync()
    {
        var userId = Context.User?.Identity?.IsAuthenticated == true ? Context.User.GetUserId().ToString() : "anonymous/agent";
        diag.Info("stream-hub", "Client connected", $"conn={Context.ConnectionId} user={userId}");
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var role = "client";
        int? sessionId = null;
        if (Context.Items.TryGetValue(PlayerSessionKey, out var p) && p is int ps)
        {
            role = "player";
            sessionId = ps;
        }
        else if (Context.Items.TryGetValue(AgentSessionKey, out var a) && a is int asid)
        {
            role = "agent";
            sessionId = asid;
        }

        var detail = $"conn={Context.ConnectionId} role={role}{(sessionId != null ? $" session={sessionId}" : "")}";
        if (exception != null)
            diag.Warn("stream-hub", "Connection dropped", $"{detail} error={exception.Message}");
        else
            diag.Info("stream-hub", "Client disconnected", detail);

        if (role == "agent" && sessionId != null)
            await telemetry.AgentDisconnectedAsync(sessionId.Value, Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Round-trip probe. Clients time the invoke themselves; the return value is the server clock (ms).</summary>
    public Task<long> Ping() => Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    [Authorize]
    public class CloudStreamHub : Hub
    {
        private readonly ILogger<CloudStreamHub> _logger;

        public CloudStreamHub(ILogger<CloudStreamHub> logger)
        {
            _logger = logger;
        }

        // Ownership was verified above (user_id = caller), so this group's only members are the session owner.
        // Join is idempotent: the client re-invokes it after every automatic reconnect (groups are per-connection).
        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Player(sessionId));
        Context.Items[PlayerSessionKey] = sessionId;
        diag.Info("stream-hub", "Player joined stream", $"session={sessionId} user={userId} conn={Context.ConnectionId}");
        await Clients.Caller.SendAsync("JoinedStream", sessionId);

        // Late joiners / reconnecting clients get the current state immediately instead of waiting for the next change.
        var snapshot = telemetry.Get(sessionId);
        if (snapshot != null)
            await Clients.Caller.SendAsync(SessionTelemetryService.TelemetryEvent, snapshot);
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
    public async Task SpectatorJoin(int sessionId)
    {
        var userId = Context.User!.GetUserId();
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT cs.session_id, cs.user_id, cs.allow_spectators, cs.is_real_stream,
                   g.name AS game_name, g.cover_url,
                   s.name AS server_name, s.region AS server_region
            FROM cloud_sessions cs
            JOIN games g ON cs.game_id=g.game_id
            LEFT JOIN cloud_servers s ON cs.server_id=s.server_id
            WHERE cs.session_id=@sid AND cs.status='active'",
            new { sid = sessionId });
        if (row == null)
        {
            var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? Context.User?.Identity?.Name;
            _logger.LogInformation("Client connected: {ConnectionId}, User: {UserId}", Context.ConnectionId, userId);
            
            if (!string.IsNullOrEmpty(userId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.GetUserGroup(userId));
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (exception != null)
            {
                _logger.LogWarning(exception, "Client disconnected with error: {ConnectionId}, User: {UserId}", Context.ConnectionId, userId);
            }
            else
            {
                _logger.LogInformation("Client disconnected gracefully: {ConnectionId}, User: {UserId}", Context.ConnectionId, userId);
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task JoinSession(string sessionId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.GetSessionGroup(sessionId));
            _logger.LogInformation("Connection {ConnectionId} joined session group {SessionId}", Context.ConnectionId, sessionId);
        }

        public async Task LeaveSession(string sessionId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, StreamGroups.GetSessionGroup(sessionId));
            _logger.LogInformation("Connection {ConnectionId} left session group {SessionId}", Context.ConnectionId, sessionId);
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Agent(sessionId));
        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Agent(sessionId));
        Context.Items[AgentSessionKey] = sessionId;
        diag.Info("stream-hub", "Agent joined stream", $"session={sessionId} server={serverId} conn={Context.ConnectionId}");
        await telemetry.AgentConnectedAsync(sessionId, Context.ConnectionId);
        await Clients.Group(StreamGroups.Player(sessionId)).SendAsync("StreamReady");
        await Clients.Caller.SendAsync("AgentJoined", sessionId);
    }

    public async Task ReportAgentTelemetry(
        int sessionId,
        int serverId,
        string? password,
        AgentStreamMetrics? metrics)
    {
        if (metrics is null || !IsValid(metrics))
            throw new HubException("Invalid stream telemetry");
        if (!await cloudServers.VerifyAccessAsync(serverId, password))
            throw new HubException("Invalid agent credentials");

        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var active = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM cloud_sessions WHERE session_id=@sid AND server_id=@srv AND status='active' AND is_real_stream=TRUE",
            new { sid = sessionId, srv = serverId });
        if (active == null)
            throw new HubException("Session not found");

        telemetry.RecordAgent(sessionId, metrics);
    }

    public async Task SendFrame(int sessionId, int serverId, string? password, string frameBase64)
    {
        if (!Context.Items.TryGetValue(AgentSessionKey, out var registered) || registered is not int registeredId || registeredId != sessionId)
        {
            diag.Warn("stream-hub", "ReportTelemetry rejected — agent not joined for session", $"session={sessionId} conn={Context.ConnectionId}");
            throw new HubException("Agent has not joined this session");
        }

        var relayTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Clients.Group(StreamGroups.Player(sessionId)).SendAsync("ReceiveFrame", frameBase64);
            if (await privacy.GetAllowSpectatorsAsync(sessionId))
                await Clients.Group(StreamGroups.Spectator(sessionId)).SendAsync("ReceiveFrame", frameBase64);
        }
        finally
        {
            telemetry.RecordRelay(sessionId, relayTimer.Elapsed.TotalMilliseconds, frameBase64.Length);
        }
    }

    public async Task SendFrame(int sessionId, int serverId, string? password, string frameBase64)
    {
        if (!Context.Items.TryGetValue(AgentSessionKey, out var registered) || registered is not int registeredId || registeredId != sessionId)
        {
            diag.Warn("stream-hub", "ReportTelemetry rejected — agent not joined for session", $"session={sessionId} conn={Context.ConnectionId}");
            throw new HubException("Agent has not joined this session");
        }

        await telemetry.ReportAgentSampleAsync(sessionId, latencyMs, fps, quality);
    }

    public record StreamInput(string Type, int? X, int? Y, string? Button, string? Key, bool? Down, int? Vk);

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
        && metrics.FramesDisplayed is >= 0 and <= metrics.FramesReceived;

    private static bool IsInRange(double value, double min, double max) =>
        double.IsFinite(value) && value >= min && value <= max;
}
