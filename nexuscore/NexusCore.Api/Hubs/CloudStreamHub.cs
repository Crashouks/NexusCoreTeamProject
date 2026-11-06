using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NexusCore.Api.Extensions;
using NexusCore.Api.Services;

namespace NexusCore.Api.Hubs;

public class CloudStreamHub(
    CloudServerService cloudServers,
    DbService db,
    CloudDiagnosticsLog diag,
    StreamPrivacyService privacy,
    StreamTelemetryService telemetry) : Hub
{
    public override Task OnConnectedAsync()
    {
        diag.Info("stream-hub", "Client connected", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception != null)
            diag.Warn("stream-hub", "Client disconnected with error", exception.Message);
        else
            diag.Debug("stream-hub", "Client disconnected", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

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

        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Player(sessionId));
        diag.Info("stream-hub", "Player joined stream", $"session={sessionId} user={userId}");
        await Clients.Caller.SendAsync("JoinedStream", sessionId);
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
        await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.Agent(sessionId));
        diag.Info("stream-hub", "Agent joined stream", $"session={sessionId} server={serverId}");
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
        if (!await cloudServers.VerifyAccessAsync(serverId, password))
        {
            diag.Warn("stream-hub", "SendFrame rejected — bad credentials", $"server={serverId}");
            return;
        }
        if (string.IsNullOrEmpty(frameBase64)) return;

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
