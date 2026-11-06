using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Hubs;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/cloud")]
public class CloudController(
    DbService db,
    SessionExpiryService sessions,
    ChatService chat,
    CloudServerService cloudServers,
    CloudDiagnosticsLog diag,
    StreamPrivacyService privacy,
    SessionTelemetryService telemetry,
    IHubContext<CloudStreamHub> streamHub) : ControllerBase
{
    private static readonly Dictionary<string, (string resolution, int fps, bool rayTracing)> PlanSpecs = new()
    {
        ["free"] = ("1080p", 60, false),
        ["starter"] = ("1080p", 60, false),
        ["pro"] = ("1440p", 120, false),
        ["ultimate"] = ("4K", 144, true),
    };

    private async Task<bool> HasAvailableCapacityAsync(MySqlConnector.MySqlConnection conn) =>
        await cloudServers.HasAvailableSlotAsync(conn);

    [HttpGet("plans")]
    [AllowAnonymous]
    public async Task<IActionResult> Plans()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var plans = await conn.QueryAsync("SELECT * FROM cloud_plans ORDER BY price_monthly ASC");
            return Ok(new
            {
                free = new
                {
                    name = "free", display_name = "Free", price_monthly = 0,
                    max_res = "1080p", max_fps = 60, ray_tracing = false, skip_queue = false,
                    description = "1 hour/day, queue required"
                },
                plans
            });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("setup-info")]
    [AllowAnonymous]
    public IActionResult SetupInfo()
    {
        var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
        var webPort = Environment.GetEnvironmentVariable("WEB_PORT") ?? "5173";
        var publicApi = Environment.GetEnvironmentVariable("PUBLIC_API_URL");
        var publicWeb = Environment.GetEnvironmentVariable("PUBLIC_WEB_URL");
        var networkMode = Environment.GetEnvironmentVariable("NETWORK_MODE") == "1"
            || Environment.GetEnvironmentVariable("API_BIND") == "0.0.0.0";
        var agentApiUrl = !string.IsNullOrWhiteSpace(publicApi)
            ? publicApi.TrimEnd('/')
            : $"http://localhost:{port}/api";
        return Ok(new
        {
            network_mode = networkMode,
            public_api_url = publicApi,
            public_web_url = publicWeb,
            agent_config_api_url = agentApiUrl,
            api_port = port,
            web_port = webPort,
            hints = new[]
            {
                "Laptop 1 (server): run start-site-network.bat",
                "Laptop 2 (agent): config.json apiUrl = agent_config_api_url",
                "Laptop 3 (player): open public_web_url in browser",
                "Different networks: set PUBLIC_API_URL + PUBLIC_WEB_URL to Tailscale or tunnel URL in nexuscore/.env",
            },
        });
    }

    [HttpPost("subscribe")]
    [Authorize]
    public async Task<IActionResult> Subscribe([FromBody] SubscribeRequest body)
    {
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var planRows = (await conn.QueryAsync<dynamic>(
                "SELECT * FROM cloud_plans WHERE name=@plan", new { plan = body.Plan }, tx)).ToList();
            if (planRows.Count == 0)
            {
                await tx.RollbackAsync();
                return ApiResults.Error(400, "Invalid plan", "VALIDATION_ERROR");
            }
            var planData = planRows[0];
            var balance = await conn.ExecuteScalarAsync<decimal>(
                "SELECT balance FROM users WHERE user_id=@uid FOR UPDATE", new { uid = User.GetUserId() }, tx);
            var price = Convert.ToDecimal(planData.price_monthly);
            if (balance < price)
            {
                await tx.RollbackAsync();
                return ApiResults.Error(402, "Insufficient balance", "INSUFFICIENT_BALANCE");
            }
            await conn.ExecuteAsync(
                "UPDATE users SET balance=balance-@price, cloud_plan=@plan, cloud_plan_expires=DATE_ADD(NOW(), INTERVAL 30 DAY) WHERE user_id=@uid",
                new { price, plan = body.Plan, uid = User.GetUserId() }, tx);
            await tx.CommitAsync();
            return Ok(new { message = "Subscribed", plan = body.Plan, expiresIn = "30 days" });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return ApiResults.Error(500, ex.Message, "SERVER_ERROR");
        }
    }

    [HttpGet("queue/status")]
    [Authorize]
    public async Task<IActionResult> QueueStatus()
    {
        try
        {
            await sessions.ExpireReadySlotsAsync();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = (await conn.QueryAsync<dynamic>(
                "SELECT * FROM cloud_queue WHERE user_id=@uid", new { uid = User.GetUserId() })).ToList();
            if (rows.Count == 0) return Ok(new { inQueue = false });
            var entry = rows[0];
            if ((string)entry.status == "ready")
            {
                return Ok(new
                {
                    inQueue = true, position = 0, status = "ready",
                    estimatedWaitMins = 0, expiresAt = entry.expires_at, gameId = entry.game_id
                });
            }
            var pos = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*)+1 FROM cloud_queue WHERE status='waiting' AND joined_at < @joined",
                new { joined = entry.joined_at });
            return Ok(new
            {
                inQueue = true, position = pos, status = entry.status,
                estimatedWaitMins = pos * 4, gameId = entry.game_id
            });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("queue/join")]
    [Authorize]
    public async Task<IActionResult> QueueJoin([FromBody] QueueJoinRequest body)
    {
        try
        {
            var userId = User.GetUserId();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var games = (await conn.QueryAsync(
                "SELECT * FROM games WHERE game_id=@gid AND status='approved' AND cloud_enabled=TRUE",
                new { gid = body.GameId })).ToList();
            if (games.Count == 0) return ApiResults.Error(404, "Game not cloud-enabled", "NOT_FOUND");

            var active = await conn.ExecuteScalarAsync<int?>(
                "SELECT session_id FROM cloud_sessions WHERE user_id=@uid AND status='active'", new { uid = userId });
            if (active != null) return ApiResults.Error(409, "Active session exists", "SESSION_ACTIVE");

            var existing = (await conn.QueryAsync("SELECT * FROM cloud_queue WHERE user_id=@uid", new { uid = userId })).ToList();
            if (existing.Count > 0) return ApiResults.Error(409, "Already in queue", "ALREADY_IN_QUEUE");

            if (await HasAvailableCapacityAsync(conn))
                return Ok(new { skipQueue = true, message = "Slot available — start session directly" });

            await conn.ExecuteAsync(
                "INSERT INTO cloud_queue (user_id, game_id, status) VALUES (@uid, @gid, 'waiting')",
                new { uid = userId, gid = body.GameId });
            var pos = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cloud_queue WHERE status='waiting'");

            await chat.JoinChatAsync(userId, ChatService.GlobalQueueLobbyChatId);
            var gameChatId = await chat.GetOrCreateGameQueueChatAsync(body.GameId);
            await chat.JoinChatAsync(userId, gameChatId);

            return StatusCode(201, new
            {
                position = pos, estimatedWaitMins = pos * 4,
                global_lobby_chat_id = ChatService.GlobalQueueLobbyChatId,
                game_chat_id = gameChatId
            });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("queue/leave")]
    [Authorize]
    public async Task<IActionResult> QueueLeave()
    {
        try
        {
            var userId = User.GetUserId();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var gameId = await conn.ExecuteScalarAsync<int?>(
                "SELECT game_id FROM cloud_queue WHERE user_id=@uid", new { uid = userId });
            await conn.ExecuteAsync("DELETE FROM cloud_queue WHERE user_id=@uid", new { uid = userId });

            await chat.LeaveChatAsync(userId, ChatService.GlobalQueueLobbyChatId);
            if (gameId.HasValue)
            {
                var gameChatId = await conn.ExecuteScalarAsync<int?>(
                    "SELECT chat_id FROM chats WHERE game_id=@gid", new { gid = gameId.Value });
                if (gameChatId.HasValue)
                    await chat.LeaveChatAsync(userId, gameChatId.Value);
            }

            return Ok(new { message = "Left queue" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("servers")]
    [Authorize]
    public async Task<IActionResult> ListServers([FromQuery] int? gameId)
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var users = (await conn.QueryAsync<dynamic>(
                "SELECT cloud_plan FROM users WHERE user_id=@uid", new { uid = User.GetUserId() })).ToList();
            var plan = users.Count > 0 ? (string)users[0].cloud_plan : "free";
            var servers = await cloudServers.ListPublicServersAsync(gameId, plan);
            var preferred = User.GetUserId(); // client stores in localStorage
            return Ok(new { servers, preferred_server_id = (int?)null });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("session/start")]
    [Authorize]
    public async Task<IActionResult> SessionStart([FromBody] SessionStartRequest body)
    {
        try
        {
            var userId = User.GetUserId();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var games = (await conn.QueryAsync<dynamic>(
                "SELECT * FROM games WHERE game_id=@gid AND status='approved' AND cloud_enabled=TRUE",
                new { gid = body.GameId })).ToList();
            if (games.Count == 0) return ApiResults.Error(404, "Game not found or not cloud-enabled", "NOT_FOUND");

            var active = await conn.ExecuteScalarAsync<int?>(
                "SELECT session_id FROM cloud_sessions WHERE user_id=@uid AND status='active'", new { uid = userId });
            if (active != null) return ApiResults.Error(409, "Active session exists", "SESSION_ACTIVE");

            var game = games[0];
            var owned = await conn.ExecuteScalarAsync<int?>(
                "SELECT 1 FROM libraries WHERE user_id=@uid AND game_id=@gid", new { uid = userId, gid = body.GameId });
            if (owned == null && !DbValue.IsTrue(game.is_free) && body.BillingMode != "free")
                return ApiResults.Error(403, "Must own game", "NOT_OWNED");

            var users = (await conn.QueryAsync<dynamic>(
                "SELECT cloud_plan, cloud_plan_expires FROM users WHERE user_id=@uid", new { uid = userId })).ToList();
            var user = users[0];
            var plan = "free";
            var maxDuration = 60;

            if (body.BillingMode == "subscription")
            {
                var cloudPlan = (string)user.cloud_plan;
                if (cloudPlan is "none" or "free")
                    return ApiResults.Error(403, "Paid plan required", "CLOUD_PLAN_REQUIRED");
                if (user.cloud_plan_expires != null && Convert.ToDateTime(user.cloud_plan_expires) < DateTime.UtcNow)
                    return ApiResults.Error(403, "Plan expired", "CLOUD_PLAN_EXPIRED");
                plan = cloudPlan;
                maxDuration = 0;
            }
            else
            {
                var usedToday = await ResetFreeDailyIfNeededAsync(conn, userId);
                if (usedToday) return ApiResults.Error(403, "Daily free hour used", "FREE_LIMIT_REACHED");

                if (!await HasAvailableCapacityAsync(conn))
                {
                    var queue = (await conn.QueryAsync<dynamic>(
                        "SELECT * FROM cloud_queue WHERE user_id=@uid AND status='ready'", new { uid = userId })).ToList();
                    if (queue.Count == 0)
                        return ApiResults.Error(403, "Must join queue first", "QUEUE_REQUIRED");
                    if (Convert.ToDateTime(queue[0].expires_at) < DateTime.UtcNow)
                    {
                        await conn.ExecuteAsync("UPDATE cloud_queue SET status='expired' WHERE user_id=@uid", new { uid = userId });
                        await sessions.PromoteQueueAsync(conn);
                        return ApiResults.Error(403, "Queue slot expired", "QUEUE_EXPIRED");
                    }
                }
                await conn.ExecuteAsync(
                    @"UPDATE users SET cloud_free_used_today=TRUE,
                      cloud_free_reset_at=DATE_ADD(CURDATE(), INTERVAL 1 DAY) WHERE user_id=@uid", new { uid = userId });
                await conn.ExecuteAsync("DELETE FROM cloud_queue WHERE user_id=@uid", new { uid = userId });
            }

            if (!body.ServerId.HasValue || body.ServerId.Value <= 0)
                return ApiResults.Error(400, "Select a streaming server in Cloud Settings", "SERVER_REQUIRED");

            var userCloudPlan = (string)user.cloud_plan;
            var (server, serverErr) = await cloudServers.ResolveServerForSessionAsync(
                conn, body.ServerId.Value, body.GameId, userCloudPlan, body.ServerPassword);
            if (server == null)
            {
                diag.Warn("session", "Session start blocked", $"user={userId} server={body.ServerId} game={body.GameId} reason={serverErr}");
                return ApiResults.Error(403, serverErr ?? "Server unavailable", "SERVER_UNAVAILABLE");
            }

            var tier = (string)server.server_tier;
            var isRealStream = tier == "real";
            int serverId = (int)server.server_id;
            string region = (string)server.region;
            string? serverName = (string)server.name;
            string? gpuModel = server.gpu_model as string;

            if (isRealStream && !await cloudServers.HasAvailableSlotAsync(conn))
            {
                var realCapacity = await conn.ExecuteScalarAsync<int>(@"
                    SELECT COALESCE(SUM(max_slots),0) FROM cloud_servers
                    WHERE server_tier='real' AND status='online'
                      AND last_heartbeat >= DATE_SUB(NOW(), INTERVAL 90 SECOND)");
                var realActive = await conn.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) FROM cloud_sessions cs
                    JOIN cloud_servers s ON cs.server_id=s.server_id
                    WHERE cs.status='active' AND s.server_tier='real'");
                if (realActive >= realCapacity)
                    return ApiResults.Error(503, "All real streaming machines are at capacity", "NO_SERVER_CAPACITY");
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var allowSpectators = body.AllowSpectators == true;
            var sessionId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO cloud_sessions (user_id, game_id, plan, max_duration_mins, stream_token, server_id, server_region, is_real_stream, allow_spectators, status)
                  VALUES (@uid, @gid, @plan, @max, @token, @sid, @region, @real, @allow, 'active');
                  SELECT LAST_INSERT_ID();",
                new { uid = userId, gid = body.GameId, plan, max = maxDuration, token, sid = serverId, region, real = isRealStream, allow = allowSpectators });

            privacy.SetCached((int)sessionId, allowSpectators);

            string? launchStatus = null;
            if (isRealStream && server.executable_path != null)
            {
                await cloudServers.EnqueueLaunchJobAsync(conn, (int)sessionId, serverId, body.GameId, (string)server.executable_path);
                launchStatus = "pending";
            }
            if (isRealStream)
                await telemetry.SetStatusAsync((int)sessionId, StreamStatus.Starting);

            diag.Info("session", "Session started", $"session={sessionId} user={userId} server={serverId} ({serverName}) real={isRealStream}");

            var specs = PlanSpecs.GetValueOrDefault(plan, PlanSpecs["free"]);
            return StatusCode(201, new
            {
                sessionId, streamToken = token,
                isRealStream,
                region,
                serverName,
                serverId,
                gpuModel,
                launchStatus,
                serverTier = tier,
                resolution = specs.resolution,
                fps = specs.fps,
                rayTracing = specs.rayTracing,
                maxDurationMins = maxDuration,
                allowSpectators,
                game = new { game_id = (int)game.game_id, name = (string)game.name, cover_url = game.cover_url }
            });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("session/end")]
    [Authorize]
    public async Task<IActionResult> SessionEnd()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var sessionsList = (await conn.QueryAsync<dynamic>(
                "SELECT * FROM cloud_sessions WHERE user_id=@uid AND status='active'", new { uid = User.GetUserId() })).ToList();
            if (sessionsList.Count == 0) return ApiResults.Error(404, "No active session", "NOT_FOUND");
            var s = sessionsList[0];
            var duration = await conn.ExecuteScalarAsync<int>(
                "SELECT TIMESTAMPDIFF(MINUTE, @start, NOW())", new { start = s.started_at });
            await conn.ExecuteAsync(
                "UPDATE cloud_sessions SET status='ended', ended_at=NOW(), duration_mins=@dur WHERE session_id=@id",
                new { dur = duration, id = (int)s.session_id });
            if (s.server_id != null)
                await cloudServers.EnqueueStopJobAsync(conn, (int)s.session_id, (int)s.server_id, (int)s.game_id);
            privacy.Invalidate((int)s.session_id);
            if (DbValue.IsTrue(s.is_real_stream))
                await telemetry.SetStatusAsync((int)s.session_id, StreamStatus.Stopped, "user_ended");
            if ((string)s.plan == "free") await sessions.PromoteQueueAsync(conn);
            return Ok(new { message = "Session ended", durationMins = duration });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("session/heartbeat")]
    [Authorize]
    public async Task<IActionResult> SessionHeartbeat()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var sessionsList = (await conn.QueryAsync<dynamic>(
                "SELECT * FROM cloud_sessions WHERE user_id=@uid AND status='active'", new { uid = User.GetUserId() })).ToList();
            if (sessionsList.Count == 0) return Ok(new { active = false });
            var s = sessionsList[0];
            var maxDur = (int)s.max_duration_mins;
            if (maxDur > 0)
            {
                var mins = await conn.ExecuteScalarAsync<int>(
                    "SELECT TIMESTAMPDIFF(MINUTE, @start, NOW())", new { start = s.started_at });
                if (mins >= maxDur)
                {
                    await conn.ExecuteAsync(
                        "UPDATE cloud_sessions SET status='expired', ended_at=NOW(), duration_mins=@dur WHERE session_id=@id",
                        new { dur = maxDur, id = (int)s.session_id });
                    if (s.server_id != null)
                        await cloudServers.EnqueueStopJobAsync(conn, (int)s.session_id, (int)s.server_id, (int)s.game_id);
                    if (DbValue.IsTrue(s.is_real_stream))
                        await telemetry.SetStatusAsync((int)s.session_id, StreamStatus.Stopped, "time_limit");
                    if ((string)s.plan == "free") await sessions.PromoteQueueAsync(conn);
                    return Ok(new { autoEnded = true, reason = "Time limit reached" });
                }
                return Ok(new { active = true, minutesRemaining = maxDur - mins });
            }
            return Ok(new { active = true, minutesRemaining = (int?)null });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("session/active")]
    [Authorize]
    public async Task<IActionResult> SessionActive()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = (await conn.QueryAsync<dynamic>(
                @"SELECT cs.*, g.name, g.cover_url, g.slug,
                         s.name AS server_name, s.gpu_model AS server_gpu
                  FROM cloud_sessions cs
                  JOIN games g ON cs.game_id=g.game_id
                  LEFT JOIN cloud_servers s ON cs.server_id=s.server_id
                  WHERE cs.user_id=@uid AND cs.status='active'", new { uid = User.GetUserId() })).ToList();
            if (rows.Count == 0) return Ok(null);
            var s = rows[0];
            var specs = PlanSpecs.GetValueOrDefault((string)s.plan, PlanSpecs["free"]);
            int? minutesRemaining = null;
            var maxDur = (int)s.max_duration_mins;
            if (maxDur > 0)
            {
                var mins = await conn.ExecuteScalarAsync<int>(
                    "SELECT TIMESTAMPDIFF(MINUTE, @start, NOW())", new { start = s.started_at });
                minutesRemaining = Math.Max(0, maxDur - mins);
            }
            var dict = PricingService.RowToDict(s);
            dict["resolution"] = specs.resolution;
            dict["fps"] = specs.fps;
            dict["rayTracing"] = specs.rayTracing;
            dict["minutesRemaining"] = minutesRemaining;
            dict["is_real_stream"] = DbValue.IsTrue(s.is_real_stream);
            dict["allow_spectators"] = DbValue.IsTrue(s.allow_spectators);
            return Ok(dict);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("streams/live")]
    [Authorize]
    public async Task<IActionResult> LiveStreams()
    {
        try
        {
            var userId = User.GetUserId();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();

            var rows = (await conn.QueryAsync<dynamic>(@"
                SELECT cs.session_id, cs.user_id, cs.allow_spectators, cs.is_real_stream, cs.started_at,
                       u.username, g.name AS game_name, g.cover_url,
                       s.name AS server_name, s.region AS server_region
                FROM cloud_sessions cs
                JOIN users u ON cs.user_id=u.user_id
                JOIN games g ON cs.game_id=g.game_id
                LEFT JOIN cloud_servers s ON cs.server_id=s.server_id
                WHERE cs.status='active'
                ORDER BY cs.started_at DESC
                LIMIT 24")).ToList();

            var viewerQueue = await GetViewerQueueSnapshotAsync(conn, userId);
            var streams = rows.Select(row =>
            {
                var isOwn = (int)row.user_id == userId;
                var allow = DbValue.IsTrue(row.allow_spectators);
                var canView = isOwn || allow;
                return new
                {
                    session_id = (int)row.session_id,
                    is_own = isOwn,
                    allow_spectators = allow,
                    can_view = canView,
                    is_real_stream = DbValue.IsTrue(row.is_real_stream),
                    server_name = row.server_name as string,
                    server_region = row.server_region as string,
                    player_username = isOwn ? row.username as string : MaskUsername(row.username as string),
                    game_name = canView ? row.game_name as string : null,
                    cover_url = canView ? row.cover_url as string : null,
                    started_at = row.started_at,
                };
            });

            return Ok(new { streams, viewer_queue = viewerQueue });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPatch("session/privacy")]
    [Authorize]
    public async Task<IActionResult> UpdateSessionPrivacy([FromBody] SessionPrivacyRequest body)
    {
        try
        {
            var userId = User.GetUserId();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var row = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT cs.session_id, cs.allow_spectators, cs.is_real_stream,
                       g.name AS game_name, g.cover_url,
                       s.name AS server_name, s.region AS server_region
                FROM cloud_sessions cs
                JOIN games g ON cs.game_id=g.game_id
                LEFT JOIN cloud_servers s ON cs.server_id=s.server_id
                WHERE cs.session_id=@sid AND cs.user_id=@uid AND cs.status='active'",
                new { sid = body.SessionId, uid = userId });
            if (row == null)
                return ApiResults.Error(404, "No active session", "NOT_FOUND");

            var allow = body.AllowSpectators == true;
            await conn.ExecuteAsync(
                "UPDATE cloud_sessions SET allow_spectators=@allow WHERE session_id=@id",
                new { allow, id = body.SessionId });
            privacy.SetCached(body.SessionId, allow);

            await streamHub.Clients.Group(StreamGroups.Spectator(body.SessionId)).SendAsync("WatchPrivacyChanged", new
            {
                session_id = body.SessionId,
                allow_spectators = allow,
                can_view = allow,
                game_name = allow ? row.game_name as string : null,
                cover_url = allow ? row.cover_url as string : null,
            });

            return Ok(new { allow_spectators = allow });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    private static async Task<object> GetViewerQueueSnapshotAsync(MySqlConnector.MySqlConnection conn, int userId)
    {
        var entry = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT status, joined_at FROM cloud_queue WHERE user_id=@uid", new { uid = userId });
        if (entry == null)
            return new { in_queue = false, position = (int?)null, estimated_wait_mins = (int?)null };

        if ((string)entry.status == "ready")
            return new { in_queue = true, position = 0, estimated_wait_mins = 0 };

        var pos = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*)+1 FROM cloud_queue WHERE status='waiting' AND joined_at < @joined",
            new { joined = entry.joined_at });
        return new { in_queue = true, position = pos, estimated_wait_mins = pos * 4 };
    }

    private static string? MaskUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return "Player";
        if (username.Length <= 2) return username[0] + "*";
        return username[0] + new string('*', Math.Min(username.Length - 2, 6)) + username[^1];
    }

    [HttpGet("session/history")]
    [Authorize]
    public async Task<IActionResult> SessionHistory([FromQuery] int page = 1, [FromQuery] int limit = 10)
    {
        try
        {
            var offset = (page - 1) * limit;
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var total = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM cloud_sessions WHERE user_id=@uid AND status!='active'", new { uid = User.GetUserId() });
            var rows = await conn.QueryAsync(
                @"SELECT cs.*, g.name, g.cover_url FROM cloud_sessions cs
                  JOIN games g ON cs.game_id=g.game_id
                  WHERE cs.user_id=@uid AND cs.status!='active'
                  ORDER BY cs.ended_at DESC LIMIT @limit OFFSET @offset",
                new { uid = User.GetUserId(), limit, offset });
            return Ok(new { sessions = rows, total, page, limit });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("sessions/all")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> SessionsAll()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var sessionRows = await conn.QueryAsync(
                @"SELECT cs.*, u.username, g.name AS game_name,
                         s.name AS server_name, s.region AS server_region_name
                  FROM cloud_sessions cs
                  JOIN users u ON cs.user_id=u.user_id JOIN games g ON cs.game_id=g.game_id
                  LEFT JOIN cloud_servers s ON cs.server_id=s.server_id
                  ORDER BY cs.started_at DESC LIMIT 100");
            var queue = await conn.QueryAsync(
                @"SELECT cq.*, u.username, g.name AS game_name FROM cloud_queue cq
                  JOIN users u ON cq.user_id=u.user_id JOIN games g ON cq.game_id=g.game_id
                  WHERE cq.status IN ('waiting','ready') ORDER BY cq.joined_at ASC");
            return Ok(new { sessions = sessionRows, queue });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("sessions/{id:int}/force-end")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> ForceEnd(int id)
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var sessionsList = (await conn.QueryAsync<dynamic>(
                "SELECT * FROM cloud_sessions WHERE session_id=@id AND status='active'", new { id })).ToList();
            if (sessionsList.Count == 0) return ApiResults.Error(404, "Session not found", "NOT_FOUND");
            var s = sessionsList[0];
            var duration = await conn.ExecuteScalarAsync<int>(
                "SELECT TIMESTAMPDIFF(MINUTE, @start, NOW())", new { start = s.started_at });
            await conn.ExecuteAsync(
                "UPDATE cloud_sessions SET status='force_ended', ended_at=NOW(), duration_mins=@dur WHERE session_id=@id",
                new { dur = duration, id });
            if (s.server_id != null)
                await cloudServers.EnqueueStopJobAsync(conn, (int)s.session_id, (int)s.server_id, (int)s.game_id);
            if (DbValue.IsTrue(s.is_real_stream))
                await telemetry.SetStatusAsync((int)s.session_id, StreamStatus.Stopped, "admin_ended");
            if ((string)s.plan == "free") await sessions.PromoteQueueAsync(conn);
            return Ok(new { message = "Session force-ended" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    private static async Task<bool> ResetFreeDailyIfNeededAsync(MySqlConnector.MySqlConnection conn, int userId)
    {
        var users = (await conn.QueryAsync<dynamic>(
            "SELECT cloud_free_used_today, cloud_free_reset_at FROM users WHERE user_id=@id", new { id = userId })).ToList();
        var u = users[0];
        if (u.cloud_free_reset_at == null || Convert.ToDateTime(u.cloud_free_reset_at) < DateTime.UtcNow)
        {
            await conn.ExecuteAsync(
                @"UPDATE users SET cloud_free_used_today=FALSE,
                  cloud_free_reset_at=DATE_ADD(CURDATE(), INTERVAL 1 DAY) WHERE user_id=@id", new { id = userId });
            return false;
        }
        return DbValue.IsTrue(u.cloud_free_used_today);
    }

    public record SubscribeRequest(string Plan);
    public record QueueJoinRequest(int GameId);
    public record SessionStartRequest(int GameId, string? BillingMode, int? ServerId, string? ServerPassword, bool? AllowSpectators);
    public record SessionPrivacyRequest(int SessionId, bool? AllowSpectators);
}
