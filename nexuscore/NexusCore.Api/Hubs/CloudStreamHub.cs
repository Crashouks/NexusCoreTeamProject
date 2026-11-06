using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NexusCore.Api.Extensions;
using NexusCore.Api.Services;

namespace NexusCore.Api.Hubs;

/// <summary>
/// Real-time channel between the web client, the API, and the Windows streaming agent.
/// Players may join only their own session, while agents authenticate with their server
/// credentials and are then bound to a single active session.
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
        if (Context.Items.TryGetValue(PlayerSessionKey, out var player) && player is int playerSessionId)
        {
            role = "player";
            sessionId = playerSessionId;
        }
        else if (Context.Items.TryGetValue(AgentSessionKey, out var agent) && agent is int agentSessionId)
        {
            role = "agent";
            sessionId = agentSessionId;
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

    /// <summary>Returns the server clock in milliseconds for client-side round-trip measurements.</summary>
    public Task<long> Ping() => Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    [Authorize]
    public async Task PlayerJoin(int sessionId)
    {
        if (sessionId <= 0)
            throw new HubException("Invalid session");

        var userId = Context.User!.GetUserId();
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var ownsSession = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM cloud_sessions
            WHERE session_id=@sid AND user_id=@uid AND status='active'",
            new { sid = sessionId, uid = userId });
        if (ownsSession == 0)
            throw new HubException("Active session not found");

        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Player(sessionId));
        Context.Items[PlayerSessionKey] = sessionId;
        diag.Info("stream-hub", "Player joined stream", $"session={sessionId} user={userId} conn={Context.ConnectionId}");
        await Clients.Caller.SendAsync("JoinedStream", sessionId);

        var snapshot = telemetry.Get(sessionId);
        if (snapshot != null)
            await Clients.Caller.SendAsync(SessionTelemetryService.TelemetryEvent, snapshot);
    }

    public async Task SpectatorJoin(int sessionId)
    {
        if (sessionId <= 0)
            throw new HubException("Invalid session");

        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT cs.session_id, cs.allow_spectators, cs.is_real_stream,
                   g.name AS game_name, g.cover_url,
                   s.name AS server_name, s.region AS server_region
            FROM cloud_sessions cs
            JOIN games g ON cs.game_id=g.game_id
            LEFT JOIN cloud_servers s ON cs.server_id=s.server_id
            WHERE cs.session_id=@sid AND cs.status='active'",
            new { sid = sessionId });
        if (row == null)
            throw new HubException("Active session not found");

        var canView = await privacy.GetAllowSpectatorsAsync(sessionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Spectator(sessionId));
        await Clients.Caller.SendAsync("WatchState", new
        {
            session_id = sessionId,
            can_view = canView,
            allow_spectators = canView,
            is_real_stream = Convert.ToBoolean(row.is_real_stream),
            game_name = canView ? row.game_name as string : null,
            cover_url = canView ? row.cover_url as string : null,
            server_name = canView ? row.server_name as string : null,
            server_region = canView ? row.server_region as string : null
        });
    }

    public async Task AgentJoin(int sessionId, int serverId, string? password)
    {
        if (sessionId <= 0 || serverId <= 0)
            throw new HubException("Invalid session or server");
        if (!await cloudServers.VerifyAccessAsync(serverId, password))
        {
            diag.Warn("stream-hub", "Agent authentication failed", $"session={sessionId} server={serverId}");
            throw new HubException("Invalid server credentials");
        }

        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var validSession = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM cloud_sessions
            WHERE session_id=@sid AND server_id=@serverId AND is_real_stream=TRUE AND status='active'",
            new { sid = sessionId, serverId });
        if (validSession == 0)
            throw new HubException("Active session not found on this server");

        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Agent(sessionId));
        Context.Items[AgentSessionKey] = sessionId;
        Context.Items[AgentServerKey] = serverId;
        diag.Info("stream-hub", "Agent joined stream", $"session={sessionId} server={serverId} conn={Context.ConnectionId}");
        await telemetry.AgentConnectedAsync(sessionId, Context.ConnectionId);
        await Clients.Group(StreamGroups.Player(sessionId)).SendAsync("StreamReady");
        await Clients.Group(StreamGroups.Spectator(sessionId)).SendAsync("StreamReady");
        await Clients.Caller.SendAsync("AgentJoined", sessionId);
    }

    /// <summary>Reports the agent's round-trip latency and current capture settings.</summary>
    public async Task ReportTelemetry(int sessionId, int latencyMs, int fps, int quality)
    {
        if (!IsJoinedAgent(sessionId))
        {
            diag.Warn("stream-hub", "ReportTelemetry rejected — agent not joined for session", $"session={sessionId} conn={Context.ConnectionId}");
            throw new HubException("Agent has not joined this session");
        }

        await telemetry.ReportAgentSampleAsync(sessionId, latencyMs, fps, quality);
    }

    public async Task SendFrame(int sessionId, int serverId, string? password, string frameBase64)
    {
        if (!IsJoinedAgent(sessionId) ||
            !Context.Items.TryGetValue(AgentServerKey, out var registeredServer) ||
            registeredServer is not int registeredServerId || registeredServerId != serverId)
        {
            diag.Warn("stream-hub", "SendFrame rejected — agent not joined for session", $"session={sessionId} conn={Context.ConnectionId}");
            throw new HubException("Agent has not joined this session");
        }
        if (string.IsNullOrWhiteSpace(frameBase64))
            throw new HubException("Frame is required");

        await Clients.Group(StreamGroups.Player(sessionId)).SendAsync("ReceiveFrame", frameBase64);
        if (await privacy.GetAllowSpectatorsAsync(sessionId))
            await Clients.Group(StreamGroups.Spectator(sessionId)).SendAsync("ReceiveFrame", frameBase64);
    }

    [Authorize]
    public async Task SendInput(int sessionId, object? input)
    {
        if (!Context.Items.TryGetValue(PlayerSessionKey, out var registered) ||
            registered is not int registeredSessionId || registeredSessionId != sessionId)
            throw new HubException("Player has not joined this session");

        await Clients.Group(StreamGroups.Agent(sessionId)).SendAsync("ReceiveInput", input);
    }

    private bool IsJoinedAgent(int sessionId) =>
        Context.Items.TryGetValue(AgentSessionKey, out var registered) &&
        registered is int registeredSessionId &&
        registeredSessionId == sessionId;
}
