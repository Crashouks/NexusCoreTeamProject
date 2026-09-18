using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController(DbService db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int limit = 30)
    {
        try
        {
            var uid = User.GetUserId();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(@"
                SELECT notification_id, type, message, link, is_read, created_at, ref_user_id, ref_game_id
                FROM notifications
                WHERE user_id = @uid
                ORDER BY created_at DESC
                LIMIT @limit", new { uid, limit = Math.Clamp(limit, 1, 100) });
            return Ok(rows);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount()
    {
        try
        {
            var uid = User.GetUserId();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var count = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM notifications WHERE user_id=@uid AND is_read=FALSE", new { uid });
            return Ok(new { count });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id)
    {
        try
        {
            var uid = User.GetUserId();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE notifications SET is_read=TRUE WHERE notification_id=@id AND user_id=@uid",
                new { id, uid });
            return Ok(new { message = "Marked read" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead()
    {
        try
        {
            var uid = User.GetUserId();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE notifications SET is_read=TRUE WHERE user_id=@uid AND is_read=FALSE", new { uid });
            return Ok(new { message = "All marked read" });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }
}
