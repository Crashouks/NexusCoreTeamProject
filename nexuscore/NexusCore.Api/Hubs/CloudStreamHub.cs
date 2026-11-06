using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NexusCore.Api.Extensions;
using NexusCore.Api.Services;

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
    public async Task PlayerJoin(int sessionId)
    {
        var userId = Context.User!.GetUserId();
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var ok = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM cloud_sessions WHERE session_id=@sid AND user_id=@uid AND status='active'",
            new { sid = sessionId, uid = userId });
        if (ok == null)
        {
            diag.Warn("stream-hub", "PlayerJoin rejected — no active session", $"session={sessionId} user={userId}");
            throw new HubException("No active session");
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
            diag.Warn("stream-hub", "SpectatorJoin rejected — session not active", $"session={sessionId}");
            throw new HubException("Session not found");
        }
        if ((int)row.user_id == userId)
            throw new HubException("Use PlayerJoin for your own session");

        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Spectator(sessionId));
        var allow = DbValue.IsTrue(row.allow_spectators);
        privacy.SetCached(sessionId, allow);

        var queueInfo = await BuildViewerQueueInfoAsync(conn, userId);
        diag.Info("stream-hub", "Spectator joined", $"session={sessionId} user={userId} allow={allow}");

        await Clients.Caller.SendAsync("WatchState", new
        {
            session_id = sessionId,
            allow_spectators = allow,
            can_view = allow,
            is_real_stream = DbValue.IsTrue(row.is_real_stream),
            server_name = row.server_name as string,
            server_region = row.server_region as string,
            game_name = allow ? row.game_name as string : null,
            cover_url = allow ? row.cover_url as string : null,
            queue_position = queueInfo.position,
            estimated_wait_mins = queueInfo.estimatedWaitMins,
            in_queue = queueInfo.inQueue,
        });

        if (allow && DbValue.IsTrue(row.is_real_stream))
            await Clients.Caller.SendAsync("StreamReady");
    }

    [Authorize]
    public async Task SendInput(int sessionId, StreamInput input)
    {
        var userId = Context.User!.GetUserId();
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var ok = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM cloud_sessions WHERE session_id=@sid AND user_id=@uid AND status='active' AND is_real_stream=TRUE",
            new { sid = sessionId, uid = userId });
        if (ok == null) return;

        await Clients.Group(StreamGroups.Agent(sessionId)).SendAsync("ReceiveInput", input);
    }

    public async Task AgentJoin(int sessionId, int serverId, string? password)
    {
        if (!await cloudServers.VerifyAccessAsync(serverId, password))
        {
            diag.Warn("stream-hub", "AgentJoin rejected — bad credentials", $"server={serverId} session={sessionId}");
            throw new HubException("Invalid agent credentials");
        }

        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var match = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM cloud_sessions WHERE session_id=@sid AND server_id=@srv AND status='active' AND is_real_stream=TRUE",
            new { sid = sessionId, srv = serverId });
        if (match == null)
        {
            diag.Warn("stream-hub", "AgentJoin rejected — session mismatch",
                $"session={sessionId} server={serverId} (session not active on this server?)");
            throw new HubException("Session not found");
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
        if (!await cloudServers.VerifyAccessAsync(serverId, password))
        {
            diag.Warn("stream-hub", "SendFrame rejected — bad credentials", $"server={serverId}");
            return;
        }
        if (string.IsNullOrEmpty(frameBase64)) return;

        await Clients.Group(StreamGroups.Player(sessionId)).SendAsync("ReceiveFrame", frameBase64);
        if (await privacy.GetAllowSpectatorsAsync(sessionId))
            await Clients.Group(StreamGroups.Spectator(sessionId)).SendAsync("ReceiveFrame", frameBase64);
    }

    private static async Task<(bool inQueue, int? position, int? estimatedWaitMins)> BuildViewerQueueInfoAsync(
        MySqlConnector.MySqlConnection conn, int userId)
    {
        var entry = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT status, joined_at FROM cloud_queue WHERE user_id=@uid", new { uid = userId });
        if (entry == null)
            return (false, null, null);

        if ((string)entry.status == "ready")
            return (true, 0, 0);

        var pos = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*)+1 FROM cloud_queue WHERE status='waiting' AND joined_at < @joined",
            new { joined = entry.joined_at });
        return (true, pos, pos * 4);
    }

    public record StreamInput(string Type, int? X, int? Y, string? Button, string? Key, bool? Down, int? Vk);
}
