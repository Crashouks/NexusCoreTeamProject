using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize]
public class AdminController(
    DbService db,
    CloudServerService cloudServers,
    CloudDiagnosticsLog diag,
    StreamTelemetryService streamTelemetry,
    IWebHostEnvironment env) : ControllerBase
{
    private bool IsAdmin => User.IsAdmin();

    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var users = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users");
            var games = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM games WHERE status='approved'");
            var pending = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM games WHERE status='pending'");
            var devReq = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM users WHERE developer_requested_at IS NOT NULL AND is_developer_approved=FALSE");
            var cloudActive = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cloud_sessions WHERE status='active'");
            var queueWaiting = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM cloud_queue WHERE status IN ('waiting','ready')");
            var cloudServersOnline = 0;
            var cloudServerSlots = 0;
            try
            {
                cloudServersOnline = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM cloud_servers WHERE status='online'");
                cloudServerSlots = await conn.ExecuteScalarAsync<int>(
                    "SELECT COALESCE(SUM(max_slots), 0) FROM cloud_servers WHERE status='online'");
            }
            catch { /* table may not exist yet */ }
            var trialsActive = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM trials WHERE status='active'");
            var trialsToday = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM trials WHERE DATE(started_at)=CURDATE()");
            var revenue = await conn.ExecuteScalarAsync<decimal>(
                "SELECT COALESCE(SUM(purchase_price), 0) FROM libraries");
            var purchases = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM libraries");
            var wishlist = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM wishlist");
            var installed = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM libraries WHERE download_status='installed'");
            var forumTopics = 0;
            try
            {
                forumTopics = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM forum_topics");
            }
            catch { /* forum tables may not exist until migration runs */ }

            var pendingFriends = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM friends WHERE status='pending'");
            return Ok(new
            {
                users,
                games,
                pending_games = pending,
                dev_requests = devReq,
                active_cloud_sessions = cloudActive,
                queue_waiting = queueWaiting,
                cloud_servers_online = cloudServersOnline,
                cloud_server_slots = cloudServerSlots,
                active_trials = trialsActive,
                trials_today = trialsToday,
                total_revenue = revenue,
                total_purchases = purchases,
                wishlist_items = wishlist,
                installed_games = installed,
                forum_topics = forumTopics,
                pending_friend_requests = pendingFriends,
            });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users()
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(@"
                SELECT u.user_id, u.username, u.email, u.role, u.cloud_plan, u.balance, u.reg_date,
                       (SELECT COUNT(*) FROM libraries l WHERE l.user_id = u.user_id) AS library_count,
                       (SELECT COUNT(*) FROM friends f WHERE (f.user_id = u.user_id OR f.friend_id = u.user_id) AND f.status='accepted') AS friend_count
                FROM users u ORDER BY u.reg_date DESC");
            return Ok(rows);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPut("users/{id:int}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] AdminUserUpdateRequest body)
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        if (id == User.GetUserId() && body.Role != null && body.Role != "admin")
            return ApiResults.Error(400, "Cannot demote your own admin account", "VALIDATION_ERROR");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var exists = await conn.ExecuteScalarAsync<int?>("SELECT user_id FROM users WHERE user_id=@id", new { id });
            if (exists == null) return ApiResults.Error(404, "User not found", "NOT_FOUND");

            if (body.Role != null)
                await conn.ExecuteAsync("UPDATE users SET role=@role WHERE user_id=@id",
                    new { role = body.Role, id });
            if (body.CloudPlan != null)
                await conn.ExecuteAsync("UPDATE users SET cloud_plan=@plan WHERE user_id=@id",
                    new { plan = body.CloudPlan, id });
            if (body.Balance.HasValue)
                await conn.ExecuteAsync("UPDATE users SET balance=@bal WHERE user_id=@id",
                    new { bal = body.Balance.Value, id });
            if (body.BalanceDelta.HasValue)
                await conn.ExecuteAsync("UPDATE users SET balance=balance+@delta WHERE user_id=@id",
                    new { delta = body.BalanceDelta.Value, id });

            var user = await conn.QuerySingleAsync(
                "SELECT user_id, username, email, role, cloud_plan, balance FROM users WHERE user_id=@id", new { id });
            return Ok(user);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("games")]
    public async Task<IActionResult> Games()
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(@"
                SELECT g.*,
                  (SELECT COUNT(*) FROM libraries l WHERE l.game_id = g.game_id) AS owners_count,
                  (SELECT COUNT(*) FROM wishlist w WHERE w.game_id = g.game_id) AS wishlist_count
                FROM games g ORDER BY g.submitted_at DESC");
            return Ok(rows);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("browse-executable")]
    public IActionResult BrowseExecutable()
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        if (!OperatingSystem.IsWindows())
            return ApiResults.Error(400, "Browse is only available when the API runs on Windows (your gaming PC). Type the path manually from other devices.", "UNSUPPORTED");
        try
        {
            var script = Path.Combine(env.ContentRootPath, "Scripts", "browse-exe.ps1");
            var path = ExecutableBrowse.PickWindowsExecutable(script);
            if (string.IsNullOrWhiteSpace(path))
                return Ok(new { cancelled = true, path = (string?)null });
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return ApiResults.Error(400, "Please select a .exe file", "VALIDATION_ERROR");
            return Ok(new { cancelled = false, path });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("games/quick-cloud")]
    public async Task<IActionResult> QuickCloudGame([FromBody] QuickCloudGameRequest body)
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        var exePath = body.ExecutablePath?.Trim();
        if (string.IsNullOrWhiteSpace(exePath))
            return ApiResults.Error(400, "Executable path is required", "VALIDATION_ERROR");
        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return ApiResults.Error(400, "Path must point to a .exe file", "VALIDATION_ERROR");

        try
        {
            var fileName = Path.GetFileNameWithoutExtension(exePath);
            var displayName = string.IsNullOrWhiteSpace(body.Name) ? HumanizeExeName(fileName) : body.Name.Trim();
            var slug = Slugify.FromName(displayName);
            var adminId = User.GetUserId();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();

            var existing = await conn.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT game_id, name, cloud_enabled, status, slug
                  FROM games WHERE slug=@slug OR LOWER(name)=LOWER(@name)
                  ORDER BY game_id ASC LIMIT 1",
                new { slug, name = displayName });

            if (existing != null)
            {
                var gid = (int)existing.game_id;
                if (!(bool)existing.cloud_enabled || (string)existing.status != "approved")
                {
                    await conn.ExecuteAsync(
                        "UPDATE games SET cloud_enabled=TRUE, status='approved' WHERE game_id=@gid",
                        new { gid });
                }
                return Ok(new
                {
                    created = false,
                    game_id = gid,
                    name = (string)existing.name,
                    executable_path = exePath,
                });
            }

            var username = await conn.ExecuteScalarAsync<string>(
                "SELECT username FROM users WHERE user_id=@id", new { id = adminId }) ?? "Admin";
            var uniqueSlug = await EnsureUniqueSlugAsync(conn, slug);
            var cover = $"https://picsum.photos/seed/{uniqueSlug}/400/560";
            var gameId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO games (name, slug, short_desc, description, genre, tags, developer_name, developer_id,
                  price, is_free, cover_url, cloud_enabled, status, download_size_gb)
                  VALUES (@name, @slug, @desc, @desc, 'Action', 'Cloud', @dev, @devId,
                  0, TRUE, @cover, TRUE, 'approved', 25);
                  SELECT LAST_INSERT_ID();",
                new
                {
                    name = displayName,
                    slug = uniqueSlug,
                    desc = $"Cloud stream from {Path.GetFileName(exePath)}",
                    dev = username,
                    devId = adminId,
                    cover,
                });

            return Ok(new
            {
                created = true,
                game_id = (int)gameId,
                name = displayName,
                executable_path = exePath,
            });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    private static string HumanizeExeName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "Cloud Game";
        var s = fileName.Replace('_', ' ').Replace('-', ' ').Trim();
        return string.IsNullOrWhiteSpace(s) ? "Cloud Game" : s;
    }

    private static async Task<string> EnsureUniqueSlugAsync(System.Data.Common.DbConnection conn, string slug)
    {
        var candidate = slug;
        var n = 1;
        while (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM games WHERE slug=@s", new { s = candidate }) > 0)
        {
            n++;
            candidate = $"{slug}-{n}";
        }
        return candidate;
    }

    [HttpGet("purchases")]
    public async Task<IActionResult> Purchases([FromQuery] int limit = 50)
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(@"
                SELECT l.purchase_date, l.purchase_price, l.download_status,
                       u.user_id, u.username, g.game_id, g.name AS game_name, g.slug
                FROM libraries l
                JOIN users u ON l.user_id = u.user_id
                JOIN games g ON l.game_id = g.game_id
                ORDER BY l.purchase_date DESC
                LIMIT @limit", new { limit = Math.Clamp(limit, 1, 200) });
            return Ok(rows);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("forums")]
    public async Task<IActionResult> Forums()
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(@"
                SELECT t.topic_id, t.title, t.created_at, u.username AS author,
                       (SELECT COUNT(*) FROM forum_posts p WHERE p.topic_id = t.topic_id) AS post_count
                FROM forum_topics t
                JOIN users u ON t.created_by = u.user_id
                ORDER BY t.created_at DESC
                LIMIT 100");
            return Ok(rows);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpDelete("forums/{topicId:int}")]
    public async Task<IActionResult> DeleteForumTopic(int topicId)
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var deleted = await conn.ExecuteAsync("DELETE FROM forum_topics WHERE topic_id=@id", new { id = topicId });
            if (deleted == 0) return ApiResults.Error(404, "Topic not found", "NOT_FOUND");
            return Ok(new { message = "Forum topic deleted" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("cloud/clear-queue")]
    public async Task<IActionResult> ClearQueue()
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var count = await conn.ExecuteAsync(
                "DELETE FROM cloud_queue WHERE status IN ('waiting','ready')");
            return Ok(new { message = "Queue cleared", removed = count });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("cloud/servers")]
    public async Task<IActionResult> CloudServers()
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            var servers = await cloudServers.ListForAdminAsync();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var capacity = await cloudServers.GetTotalCapacityAsync(conn);
            var active = await cloudServers.GetActiveSessionCountAsync(conn);
            return Ok(new { servers, capacity, active_sessions = active, available_slots = Math.Max(0, capacity - active) });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("cloud/diagnostics")]
    public IActionResult CloudDiagnostics()
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        var logs = diag.Recent(120).Select(e => new
        {
            at = e.At,
            level = e.Level,
            source = e.Source,
            message = e.Message,
            detail = e.Detail,
        });
        return Ok(new
        {
            logs,
            stream_metrics = streamTelemetry.Recent(),
            agent_log_file = "nexuscore/agent/logs/agent.log",
            api_console = "Also check the start-site-network.bat window for [cloud:...] lines",
        });
    }

    [HttpPost("cloud/servers")]
    public async Task<IActionResult> CreateCloudServer([FromBody] CloudServerRequest body)
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        var err = ValidateCloudServerRequest(body);
        if (err != null) return ApiResults.Error(400, err, "VALIDATION_ERROR");
        try
        {
            var id = await cloudServers.CreateAsync(new CloudServerService.CloudServerInput(
                body.Name!, body.Host!, body.Region, body.GpuModel,
                body.MaxSlots ?? 1, body.AccountUsername, body.AccountSecret,
                body.AccessPassword, body.PlayerPassword, body.ServerTier ?? "real",
                body.Status, body.Notes));
            var server = await cloudServers.GetByIdAsync(id);
            return StatusCode(201, server);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPut("cloud/servers/{id:int}")]
    public async Task<IActionResult> UpdateCloudServer(int id, [FromBody] CloudServerUpdateRequest body)
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        if (string.IsNullOrWhiteSpace(body.Name) && string.IsNullOrWhiteSpace(body.Host)
            && body.MaxSlots == null && body.AccountUsername == null
            && body.AccountSecret == null && body.AccessPassword == null && body.PlayerPassword == null
            && body.ServerTier == null && body.Status == null
            && body.Region == null && body.GpuModel == null && body.Notes == null)
            return ApiResults.Error(400, "No fields to update", "VALIDATION_ERROR");
        var pwdErr = await ValidateCloudServerUpdateAsync(cloudServers, id, body);
        if (pwdErr != null) return ApiResults.Error(400, pwdErr, "VALIDATION_ERROR");
        try
        {
            var ok = await cloudServers.UpdateAsync(id, new CloudServerService.CloudServerUpdate(
                body.Name, body.Host, body.Region, body.GpuModel,
                body.MaxSlots, body.AccountUsername, body.AccountSecret,
                body.AccessPassword, body.PlayerPassword, body.ServerTier,
                body.Status, body.Notes));
            if (!ok) return ApiResults.Error(404, "Server not found", "NOT_FOUND");
            var server = await cloudServers.GetByIdAsync(id);
            return Ok(server);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpDelete("cloud/servers/{id:int}")]
    public async Task<IActionResult> DeleteCloudServer(int id)
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            var (ok, error) = await cloudServers.DeleteAsync(id);
            if (!ok) return ApiResults.Error(error == "Server not found" ? 404 : 409, error!, error == "Server not found" ? "NOT_FOUND" : "CONFLICT");
            return Ok(new { message = "Server removed" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("cloud/servers/{id:int}/games")]
    public async Task<IActionResult> CloudServerGames(int id)
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            if (await cloudServers.GetByIdAsync(id) == null)
                return ApiResults.Error(404, "Server not found", "NOT_FOUND");
            var mappings = await cloudServers.ListGameMappingsAsync(id);
            return Ok(new { mappings });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPut("cloud/servers/{id:int}/games")]
    public async Task<IActionResult> SetCloudServerGames(int id, [FromBody] CloudServerGamesRequest body)
    {
        if (!IsAdmin) return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            if (await cloudServers.GetByIdAsync(id) == null)
                return ApiResults.Error(404, "Server not found", "NOT_FOUND");
            var items = (body.Mappings ?? [])
                .Select(m => new CloudServerService.GameMappingInput(m.GameId, m.ExecutablePath ?? ""))
                .Where(m => m.GameId > 0);
            await cloudServers.SetGameMappingsAsync(id, items);
            var mappings = await cloudServers.ListGameMappingsAsync(id);
            return Ok(new { mappings });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    private static string? ValidateCloudServerRequest(CloudServerRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Name)) return "Server name is required";
        if (string.IsNullOrWhiteSpace(body.Host)) return "Host is required";
        if ((body.MaxSlots ?? 1) < 1) return "Max slots must be at least 1";
        var tier = body.ServerTier?.Trim().ToLowerInvariant() ?? "real";
        if (tier == "real" && string.IsNullOrWhiteSpace(body.AccessPassword))
            return "Agent password is required for real (Private PC) servers";
        if (tier == "real" && body.AccessPassword!.Trim().Length < 8)
            return "Agent password must be at least 8 characters";
        return null;
    }

    private static async Task<string?> ValidateCloudServerUpdateAsync(
        CloudServerService cloudServers, int id, CloudServerUpdateRequest body)
    {
        if (body.AccessPassword != null && !string.IsNullOrWhiteSpace(body.AccessPassword)
            && body.AccessPassword.Trim().Length < 8)
            return "Agent password must be at least 8 characters";
        if (body.AccessPassword == null) return null;
        if (!string.IsNullOrWhiteSpace(body.AccessPassword)) return null;
        var server = await cloudServers.GetByIdAsync(id);
        if (server == null) return null;
        var tier = ((string?)server.server_tier) ?? "real";
        if (tier == "real")
            return "Agent password cannot be cleared on a real server — set a new password instead";
        return null;
    }

    public record AdminUserUpdateRequest(string? Role, string? CloudPlan, decimal? Balance, decimal? BalanceDelta);
    public record CloudServerRequest(
        string? Name, string? Host, string? Region, string? GpuModel,
        int? MaxSlots, string? AccountUsername, string? AccountSecret,
        string? AccessPassword, string? PlayerPassword, string? ServerTier,
        string? Status, string? Notes);
    public record CloudServerUpdateRequest(
        string? Name, string? Host, string? Region, string? GpuModel,
        int? MaxSlots, string? AccountUsername, string? AccountSecret,
        string? AccessPassword, string? PlayerPassword, string? ServerTier,
        string? Status, string? Notes);
    public record CloudServerGamesRequest(IEnumerable<GameMappingDto>? Mappings);
    public record GameMappingDto(int GameId, string? ExecutablePath);
    public record QuickCloudGameRequest(string? ExecutablePath, string? Name);
}
