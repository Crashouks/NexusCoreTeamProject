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
    SessionTelemetryService telemetry) : Hub
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
        Context.Items[AgentSessionKey] = sessionId;
        diag.Info("stream-hub", "Agent joined stream", $"session={sessionId} server={serverId} conn={Context.ConnectionId}");
        await telemetry.AgentConnectedAsync(sessionId, Context.ConnectionId);
        await Clients.Group(StreamGroups.Player(sessionId)).SendAsync("StreamReady");
        await Clients.Caller.SendAsync("AgentJoined", sessionId);
    }

    /// <summary>
    /// Periodic sample from the agent: its measured round-trip to this hub plus current fps / JPEG quality.
    /// Only accepted from a connection that completed <see cref="AgentJoin"/> for the same session.
    /// </summary>
    public async Task ReportTelemetry(int sessionId, int latencyMs, int fps, int quality)
    {
        if (!Context.Items.TryGetValue(AgentSessionKey, out var registered) || registered is not int registeredId || registeredId != sessionId)
        {
            diag.Warn("stream-hub", "ReportTelemetry rejected — agent not joined for session", $"session={sessionId} conn={Context.ConnectionId}");
            throw new HubException("Agent has not joined this session");
        }

        await telemetry.ReportAgentSampleAsync(sessionId, latencyMs, fps, quality);
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

    public async Task SendFrame(int sessionId, int serverId, string? password, string frameBase64)
    {
        if (!await cloudServers.VerifyAccessAsync(serverId, password))
        {
            _logger.LogDebug("Session {SessionId} status updated to {Status}, latency {Latency}ms", sessionId, status, latencyMs);
            
            await Clients.Group(StreamGroups.GetSessionGroup(sessionId)).SendAsync("ReceiveSessionUpdate", new
            {
                SessionId = sessionId,
                Status = status,
                LatencyMs = latencyMs,
                Timestamp = DateTime.UtcNow
            });
        }
    }
}
