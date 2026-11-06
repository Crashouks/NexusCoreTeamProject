using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Security;
using Microsoft.AspNetCore.RateLimiting;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController(DbService db, NotificationService notifications) : ControllerBase
{
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var users = (await conn.QueryAsync(
                @"SELECT user_id, username, email, avatar_url, bio, country, reg_date, balance, role,
                         cloud_plan, cloud_plan_expires, cloud_free_used_today, cloud_free_reset_at,
                         developer_company, is_developer_approved
                  FROM users WHERE user_id=@id", new { id = User.GetUserId() })).ToList();
            if (users.Count == 0) return ApiResults.Error(404, "User not found", "NOT_FOUND");
            return Ok(users[0]);
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpGet("countries")]
    [AllowAnonymous]
    public async Task<IActionResult> Countries()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = (await conn.QueryAsync<string>(
                "SELECT DISTINCT country FROM users WHERE country IS NOT NULL AND TRIM(country) != '' ORDER BY country"))
                .ToList();
            var defaults = new[]
            {
                "United States", "United Kingdom", "Canada", "Germany", "France", "Poland", "Ukraine",
                "Japan", "South Korea", "Australia", "Brazil", "Mexico", "Spain", "Italy", "Netherlands",
                "Sweden", "Norway", "Finland", "Turkey", "India",
            };
            var merged = defaults.Union(rows, StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
            return Ok(merged);
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q)
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(
                "SELECT user_id, username, avatar_url FROM users WHERE username LIKE @q LIMIT 20",
                new { q = $"%{q ?? ""}%" });
            return Ok(rows);
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpGet("developer-requests")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> DeveloperRequests()
    {
        if (User.GetRole() != "admin") return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(
                @"SELECT user_id, username, email, developer_company, developer_requested_at
                  FROM users WHERE developer_requested_at IS NOT NULL AND is_developer_approved=FALSE AND role='user'");
            return Ok(rows);
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpPost("request-developer")]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<IActionResult> RequestDeveloper([FromBody] DeveloperRequest body)
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE users SET developer_company=@c, developer_requested_at=NOW() WHERE user_id=@id",
                new { c = body.Company, id = User.GetUserId() });
            return Ok(new { message = "Application submitted" });
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpPost("approve-developer/{id:int}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> ApproveDeveloper(int id)
    {
        if (User.GetRole() != "admin") return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync("UPDATE users SET role='developer', is_developer_approved=TRUE WHERE user_id=@id", new { id });
            return Ok(new { message = "Developer approved" });
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpPost("reject-developer/{id:int}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> RejectDeveloper(int id)
    {
        if (User.GetRole() != "admin") return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE users SET developer_requested_at=NULL, developer_company=NULL WHERE user_id=@id", new { id });
            return Ok(new { message = "Developer rejected" });
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetUser(int id)
    {
        if (!CanAccess(id)) return ApiResults.Error(403, "Access denied", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var users = (await conn.QueryAsync(
                @"SELECT user_id, username, email, avatar_url, bio, country, reg_date, balance, role,
                         cloud_plan, cloud_plan_expires FROM users WHERE user_id=@id", new { id })).ToList();
            if (users.Count == 0) return ApiResults.Error(404, "User not found", "NOT_FOUND");
            return Ok(users[0]);
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] ProfileUpdate body)
    {
        if (!CanAccess(id)) return ApiResults.Error(403, "Access denied", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var current = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT bio, country, avatar_url FROM users WHERE user_id=@id", new { id });
            if (current == null) return ApiResults.Error(404, "User not found", "NOT_FOUND");

            var bio = body.Bio ?? (string?)current.bio;
            var country = body.Country ?? (string?)current.country;
            var avatar = body.AvatarUrl ?? (string?)current.avatar_url;

            await conn.ExecuteAsync(
                "UPDATE users SET bio=@bio, country=@country, avatar_url=@avatar WHERE user_id=@id",
                new { bio, country, avatar, id });
            return Ok(new { message = "Profile updated", avatar_url = avatar });
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        if (User.GetRole() != "admin") return ApiResults.Error(403, "Insufficient permissions", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM users WHERE user_id=@id", new { id });
            return Ok(new { message = "User deleted" });
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpGet("{id:int}/library")]
    public async Task<IActionResult> Library(int id)
    {
        if (!CanAccess(id)) return ApiResults.Error(403, "Access denied", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(
                @"SELECT g.*, l.purchase_date, l.purchase_price, l.playtime_mins, l.last_played,
                         l.download_status, l.download_progress,
                         COALESCE(g.download_size_gb, 25) AS download_size_gb
                  FROM libraries l JOIN games g ON l.game_id=g.game_id
                  WHERE l.user_id=@id ORDER BY l.purchase_date DESC", new { id });
            return Ok(rows);
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpPost("{id:int}/library/{gameId:int}")]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<IActionResult> Purchase(int id, int gameId, [FromBody] PurchaseRequest? body)
    {
        if (!CanAccess(id)) return ApiResults.Error(403, "Access denied", "FORBIDDEN");
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var existing = (await conn.QueryAsync(
                "SELECT * FROM libraries WHERE user_id=@uid AND game_id=@gid", new { uid = id, gid = gameId }, tx)).ToList();
            if (existing.Count > 0)
            {
                await tx.RollbackAsync();
                return ApiResults.Error(409, "Already in library", "ALREADY_OWNED");
            }
            var games = (await conn.QueryAsync<dynamic>(
                "SELECT * FROM games WHERE game_id=@gid AND status='approved'", new { gid = gameId }, tx)).ToList();
            if (games.Count == 0)
            {
                await tx.RollbackAsync();
                return ApiResults.Error(404, "Game not found", "NOT_FOUND");
            }
            var game = games[0];
            decimal price = 0;
            if (!DbValue.IsTrue(game.is_free) && Convert.ToDecimal(game.price) > 0)
            {
                var applyTrial = false;
                if (body?.ApplyDiscount == true)
                {
                    var trial = (await conn.QueryAsync(
                        "SELECT status FROM trials WHERE user_id=@uid AND game_id=@gid AND status IN ('completed','active')",
                        new { uid = id, gid = gameId }, tx)).ToList();
                    applyTrial = trial.Count > 0;
                }
                price = PricingService.GetPurchasePrice(game, applyTrial);
                var balance = await conn.ExecuteScalarAsync<decimal>(
                    "SELECT balance FROM users WHERE user_id=@uid FOR UPDATE", new { uid = id }, tx);
                if (balance < price)
                {
                    await tx.RollbackAsync();
                    return ApiResults.Error(402, "Insufficient balance", "INSUFFICIENT_BALANCE");
                }
                await conn.ExecuteAsync("UPDATE users SET balance=balance-@price WHERE user_id=@uid",
                    new { price, uid = id }, tx);
            }
            await conn.ExecuteAsync(
                "INSERT INTO libraries (user_id, game_id, purchase_price, download_status, download_progress) VALUES (@uid, @gid, @price, 'none', 0)",
                new { uid = id, gid = gameId, price }, tx);
            await conn.ExecuteAsync(
                @"UPDATE trials SET status='purchased', ended_at=COALESCE(ended_at, NOW())
                  WHERE user_id=@uid AND game_id=@gid AND status IN ('active','completed')",
                new { uid = id, gid = gameId }, tx);
            var gameName = (string?)game.name ?? "a game";
            var gameSlug = (string?)game.slug;
            await notifications.CreateAsync(conn, tx, id, "purchase",
                $"You purchased {gameName}",
                gameSlug != null ? $"/games/{gameSlug}" : "/library",
                refGameId: gameId);
            await tx.CommitAsync();
            var newBalance = await conn.ExecuteScalarAsync<decimal>("SELECT balance FROM users WHERE user_id=@uid", new { uid = id });
            return StatusCode(201, new { message = "Purchased", price, balance = newBalance });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return ApiResults.ServerError(ex);
        }
    }

    [HttpDelete("{id:int}/library/{gameId:int}")]
    public async Task<IActionResult> RemoveFromLibrary(int id, int gameId)
    {
        if (!CanAccess(id)) return ApiResults.Error(403, "Access denied", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM libraries WHERE user_id=@uid AND game_id=@gid", new { uid = id, gid = gameId });
            return Ok(new { message = "Removed from library" });
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpGet("{id:int}/friends")]
    public async Task<IActionResult> Friends(int id)
    {
        if (!CanAccess(id)) return ApiResults.Error(403, "Access denied", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(
                @"SELECT u.user_id, u.username, u.avatar_url, f.status, f.created_at
                  FROM friends f JOIN users u ON (
                    CASE WHEN f.user_id=@id THEN f.friend_id ELSE f.user_id END = u.user_id
                  )
                  WHERE (f.user_id=@id OR f.friend_id=@id) AND f.status='accepted'", new { id });
            return Ok(rows);
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpGet("{id:int}/friend-requests")]
    public async Task<IActionResult> FriendRequests(int id)
    {
        if (!CanAccess(id)) return ApiResults.Error(403, "Access denied", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(@"
                SELECT u.user_id, u.username, u.avatar_url, f.created_at
                FROM friends f
                JOIN users u ON f.user_id = u.user_id
                WHERE f.friend_id = @id AND f.status = 'pending'
                ORDER BY f.created_at DESC", new { id });
            return Ok(rows);
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpPost("{id:int}/friends/{friendId:int}")]
    public async Task<IActionResult> AddFriend(int id, int friendId)
    {
        if (!CanAccess(id)) return ApiResults.Error(403, "Access denied", "FORBIDDEN");
        if (id == friendId) return ApiResults.Error(400, "Cannot add yourself", "VALIDATION_ERROR");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var existing = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT user_id, friend_id, status FROM friends
                WHERE (user_id=@uid AND friend_id=@fid) OR (user_id=@fid AND friend_id=@uid)",
                new { uid = id, fid = friendId }, tx);
            if (existing != null)
            {
                var status = existing.status?.ToString() ?? "";
                if (status == "accepted")
                {
                    await tx.RollbackAsync();
                    return ApiResults.Error(409, "Already friends", "DUPLICATE");
                }
                if (status == "pending")
                {
                    var incoming = Convert.ToInt32(existing.user_id) == friendId;
                    if (incoming)
                    {
                        await conn.ExecuteAsync(
                            "UPDATE friends SET status='accepted' WHERE user_id=@fid AND friend_id=@uid",
                            new { uid = id, fid = friendId }, tx);
                        var myName = await conn.ExecuteScalarAsync<string>(
                            "SELECT username FROM users WHERE user_id=@uid", new { uid = id }, tx) ?? "Someone";
                        var theirName = await conn.ExecuteScalarAsync<string>(
                            "SELECT username FROM users WHERE user_id=@fid", new { fid = friendId }, tx) ?? "User";
                        await tx.CommitAsync();
                        await SafeNotifyAsync(friendId, "friend_added",
                            $"{myName} accepted your friend request", "/community?tab=friends", id);
                        await SafeNotifyAsync(id, "friend_added",
                            $"You and {theirName} are now friends", "/community?tab=friends", friendId);
                        return Ok(new { message = "Friend request accepted" });
                    }
                    await tx.RollbackAsync();
                    return ApiResults.Error(409, "Friend request already sent", "DUPLICATE");
                }
            }

            var requesterName = await conn.ExecuteScalarAsync<string>(
                "SELECT username FROM users WHERE user_id=@uid", new { uid = id }, tx) ?? "Someone";
            await conn.ExecuteAsync(
                "INSERT INTO friends (user_id, friend_id, status) VALUES (@uid, @fid, 'pending')",
                new { uid = id, fid = friendId }, tx);
            await tx.CommitAsync();
            await SafeNotifyAsync(friendId, "friend_request",
                $"{requesterName} sent you a friend request", "/community?tab=friends", id);
            return StatusCode(201, new { message = "Friend request sent" });
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1062)
        {
            return ApiResults.Error(409, "Request already exists", "DUPLICATE");
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpPost("{id:int}/friends/{friendId:int}/accept")]
    public async Task<IActionResult> AcceptFriend(int id, int friendId)
    {
        if (!CanAccess(id)) return ApiResults.Error(403, "Access denied", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var updated = await conn.ExecuteAsync(@"
                UPDATE friends SET status='accepted'
                WHERE user_id=@fid AND friend_id=@uid AND status='pending'",
                new { uid = id, fid = friendId }, tx);
            if (updated == 0)
            {
                await tx.RollbackAsync();
                return ApiResults.Error(404, "Friend request not found", "NOT_FOUND");
            }
            var myName = await conn.ExecuteScalarAsync<string>(
                "SELECT username FROM users WHERE user_id=@uid", new { uid = id }, tx) ?? "Someone";
            var theirName = await conn.ExecuteScalarAsync<string>(
                "SELECT username FROM users WHERE user_id=@fid", new { fid = friendId }, tx) ?? "User";
            await tx.CommitAsync();
            await SafeNotifyAsync(friendId, "friend_added",
                $"{myName} accepted your friend request", "/community?tab=friends", id);
            await SafeNotifyAsync(id, "friend_added",
                $"You and {theirName} are now friends", "/community?tab=friends", friendId);
            return Ok(new { message = "Friend request accepted" });
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpDelete("{id:int}/friends/{friendId:int}/reject")]
    public async Task<IActionResult> RejectFriend(int id, int friendId)
    {
        if (!CanAccess(id)) return ApiResults.Error(403, "Access denied", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var deleted = await conn.ExecuteAsync(@"
                DELETE FROM friends WHERE user_id=@fid AND friend_id=@uid AND status='pending'",
                new { uid = id, fid = friendId });
            if (deleted == 0) return ApiResults.Error(404, "Friend request not found", "NOT_FOUND");
            return Ok(new { message = "Friend request declined" });
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpDelete("{id:int}/friends/{friendId:int}")]
    public async Task<IActionResult> RemoveFriend(int id, int friendId)
    {
        if (!CanAccess(id)) return ApiResults.Error(403, "Access denied", "FORBIDDEN");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "DELETE FROM friends WHERE (user_id=@uid AND friend_id=@fid) OR (user_id=@fid AND friend_id=@uid)",
                new { uid = id, fid = friendId });
            return Ok(new { message = "Friend removed" });
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    [HttpPost("{id:int}/topup")]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<IActionResult> Topup(int id, [FromBody] TopupRequest body)
    {
        if (!CanAccess(id)) return ApiResults.Error(403, "Access denied", "FORBIDDEN");
        if (body.Amount <= 0) return ApiResults.Error(400, "Invalid amount", "VALIDATION_ERROR");
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync("UPDATE users SET balance=balance+@amt WHERE user_id=@id", new { amt = body.Amount, id });
            var balance = await conn.ExecuteScalarAsync<decimal>("SELECT balance FROM users WHERE user_id=@id", new { id });
            return Ok(new { balance });
        }
        catch (Exception ex) { return ApiResults.ServerError(ex); }
    }

    private bool CanAccess(int targetId) => User.GetRole() == "admin" || User.GetUserId() == targetId;

    private async Task SafeNotifyAsync(int userId, string type, string message, string? link = null, int? refUserId = null)
    {
        try
        {
            await notifications.CreateAsync(userId, type, message, link, refUserId);
        }
        catch
        {
            // Friend actions should succeed even if notifications table is unavailable.
        }
    }

    public record DeveloperRequest(string? Company);
    public record ProfileUpdate(string? Bio, string? Country, string? AvatarUrl);
    public record PurchaseRequest(bool ApplyDiscount);
    public record TopupRequest(decimal Amount);
}
