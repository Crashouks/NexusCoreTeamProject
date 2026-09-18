using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/forums")]
public class ForumsController(DbService db) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> List()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(@"
                SELECT t.topic_id, t.title, t.created_at,
                       u.username AS author,
                       (SELECT COUNT(*) FROM forum_posts p WHERE p.topic_id = t.topic_id) AS post_count,
                       (SELECT MAX(p.created_at) FROM forum_posts p WHERE p.topic_id = t.topic_id) AS last_post_at
                FROM forum_topics t
                JOIN users u ON t.created_by = u.user_id
                ORDER BY COALESCE(last_post_at, t.created_at) DESC");
            return Ok(rows);
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("{topicId:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> Detail(int topicId)
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var topic = await conn.QueryFirstOrDefaultAsync(@"
                SELECT t.topic_id, t.title, t.created_at, u.username AS author
                FROM forum_topics t
                JOIN users u ON t.created_by = u.user_id
                WHERE t.topic_id = @id", new { id = topicId });
            if (topic == null) return ApiResults.Error(404, "Topic not found", "NOT_FOUND");

            var posts = await conn.QueryAsync(@"
                SELECT p.post_id, p.content, p.created_at, u.username, u.user_id
                FROM forum_posts p
                JOIN users u ON p.user_id = u.user_id
                WHERE p.topic_id = @id
                ORDER BY p.created_at ASC", new { id = topicId });

            return Ok(new { topic, posts });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] CreateTopicRequest body)
    {
        try
        {
            var title = body.Title?.Trim();
            if (string.IsNullOrWhiteSpace(title))
                return ApiResults.Error(400, "Title is required", "VALIDATION_ERROR");
            if (title.Length > 200)
                return ApiResults.Error(400, "Title too long", "VALIDATION_ERROR");

            var uid = User.GetUserId();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var topicId = await conn.ExecuteScalarAsync<int>(@"
                INSERT INTO forum_topics (title, created_by) VALUES (@title, @uid);
                SELECT LAST_INSERT_ID();",
                new { title, uid });

            return Ok(new { topic_id = topicId, title });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("{topicId:int}/posts")]
    [Authorize]
    public async Task<IActionResult> AddPost(int topicId, [FromBody] CreatePostRequest body)
    {
        try
        {
            var content = body.Content?.Trim();
            if (string.IsNullOrWhiteSpace(content))
                return ApiResults.Error(400, "Message is required", "VALIDATION_ERROR");

            var uid = User.GetUserId();
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var exists = await conn.ExecuteScalarAsync<int?>(
                "SELECT topic_id FROM forum_topics WHERE topic_id=@id", new { id = topicId });
            if (exists == null) return ApiResults.Error(404, "Topic not found", "NOT_FOUND");

            var postId = await conn.ExecuteScalarAsync<int>(@"
                INSERT INTO forum_posts (topic_id, user_id, content) VALUES (@tid, @uid, @content);
                SELECT LAST_INSERT_ID();",
                new { tid = topicId, uid, content });

            var username = User.Identity?.Name ?? "user";
            return Ok(new
            {
                post_id = postId,
                topic_id = topicId,
                user_id = uid,
                username,
                content,
                created_at = DateTime.UtcNow
            });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    public record CreateTopicRequest(string Title);
    public record CreatePostRequest(string Content);
}
