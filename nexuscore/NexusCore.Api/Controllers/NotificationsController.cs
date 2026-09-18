using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

/// <summary>
/// Manages the caller's in-app notifications: listing, unread counts, and marking
/// individual notifications or all notifications as read. All endpoints require authentication.
/// </summary>
[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController(DbService db) : ControllerBase
{
    /// <summary>
    /// Lists the caller's most recent notifications, newest first.
    /// </summary>
    /// <param name="limit">Query parameter: maximum number of notifications to return. Clamped to 1–100. Defaults to 30.</param>
    /// <response code="200">Returns the caller's notifications.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Returns the count of unread notifications for the caller.
    /// </summary>
    /// <response code="200">Returns the unread notification count.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpGet("unread-count")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Marks a single notification belonging to the caller as read.
    /// </summary>
    /// <remarks>Succeeds even if the notification does not exist or belongs to another user (no-op update).</remarks>
    /// <param name="id">Route parameter: the notification to mark as read.</param>
    /// <response code="200">Notification marked as read (or the update was a no-op).</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost("{id:int}/read")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Marks all of the caller's unread notifications as read.
    /// </summary>
    /// <response code="200">All unread notifications marked as read.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost("read-all")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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