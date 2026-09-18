using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/media")]
public class MediaController(DbService db, MediaFileService files) : ControllerBase
{
    private static readonly string[] AllowedTypes = ["image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif"];
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    [HttpPost("upload")]
    [Authorize(Roles = "developer,admin")]
    public async Task<IActionResult> Upload(IFormFile? file, [FromForm] int game_id, [FromForm] string? media_type)
    {
        if (file == null) return ApiResults.Error(400, "No file", "VALIDATION_ERROR");
        if (game_id <= 0) return ApiResults.Error(400, "game_id is required", "VALIDATION_ERROR");
        if (!AllowedTypes.Contains(file.ContentType.ToLowerInvariant()))
            return ApiResults.Error(400, "Only image files allowed", "VALIDATION_ERROR");

        try
        {
            if (!await CanManageGameAsync(game_id))
                return ApiResults.Error(403, "Access denied", "FORBIDDEN");

            var (fileName, _, url) = await files.SaveGameImageAsync(game_id, file);
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE games SET cover_url=@url WHERE game_id=@gid",
                new { url, gid = game_id });
            await conn.ExecuteAsync(
                "INSERT INTO game_media (game_id, media_type, url) VALUES (@gid, @type, @url)",
                new { gid = game_id, type = media_type ?? "image", url });
            return Ok(new { url, fileName, size = file.Length, mimetype = file.ContentType, game_id });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    /// <summary>Download game image file (e.g. game_image_35.jpg).</summary>
    [HttpGet("game/{gameId:int}")]
    [AllowAnonymous]
    public IActionResult GetGameImage(int gameId)
    {
        var path = files.FindGameImagePath(gameId);
        if (path == null) return ApiResults.Error(404, "File not found", "NOT_FOUND");
        if (!ContentTypes.TryGetContentType(path, out var contentType))
            contentType = "application/octet-stream";
        return PhysicalFile(path, contentType, Path.GetFileName(path));
    }

    [HttpGet("validate-url")]
    [Authorize]
    public async Task<IActionResult> ValidateUrl([FromQuery] string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ApiResults.Error(400, "URL required", "VALIDATION_ERROR");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client.SendAsync(request);
            var ct = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return new ObjectResult(new { valid = false, error = "URL is not an image", code = "NOT_IMAGE" }) { StatusCode = 400 };
            return Ok(new { valid = true, contentType = ct });
        }
        catch
        {
            return new ObjectResult(new { valid = false, error = "Could not validate URL", code = "INVALID_URL" }) { StatusCode = 400 };
        }
    }

    [HttpPost("avatar")]
    [Authorize]
    public async Task<IActionResult> UploadAvatar(IFormFile? file)
    {
        if (file == null) return ApiResults.Error(400, "No file", "VALIDATION_ERROR");
        if (!AllowedTypes.Contains(file.ContentType.ToLowerInvariant()))
            return ApiResults.Error(400, "Only image files allowed (JPEG, PNG, WebP, GIF)", "VALIDATION_ERROR");
        if (file.Length > MediaFileService.AvatarMaxBytes)
            return ApiResults.Error(400, "Avatar must be 10 MB or smaller", "FILE_TOO_LARGE");

        try
        {
            var userId = User.GetUserId();
            var (_, _, url) = await files.SaveAvatarAsync(userId, file);
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE users SET avatar_url=@url WHERE user_id=@uid",
                new { url, uid = userId });
            return Ok(new { url, avatar_url = url, size = file.Length, mimetype = file.ContentType });
        }
        catch (InvalidOperationException ex)
        {
            return ApiResults.Error(400, ex.Message, "FILE_TOO_LARGE");
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    private async Task<bool> CanManageGameAsync(int gameId)
    {
        if (User.GetRole() == "admin") return true;
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var owner = await conn.ExecuteScalarAsync<int?>(
            "SELECT developer_id FROM games WHERE game_id=@id", new { id = gameId });
        return owner == User.GetUserId();
    }
}

[ApiController]
[Route("api")]
[Authorize]
public class UploadController(DbService db, MediaFileService files) : ControllerBase
{
    private static readonly string[] AllowedTypes = ["image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif"];

    [HttpPost("upload")]
    [Authorize(Roles = "developer,admin")]
    public async Task<IActionResult> Upload(IFormFile? file, [FromForm] int game_id)
    {
        if (file == null) return ApiResults.Error(400, "No file", "VALIDATION_ERROR");
        if (game_id <= 0) return ApiResults.Error(400, "game_id is required", "VALIDATION_ERROR");
        if (!AllowedTypes.Contains(file.ContentType.ToLowerInvariant()))
            return ApiResults.Error(400, "Only image files allowed", "VALIDATION_ERROR");
        try
        {
            if (User.GetRole() != "admin")
            {
                await using var conn = db.CreateConnection();
                await conn.OpenAsync();
                var owner = await conn.ExecuteScalarAsync<int?>(
                    "SELECT developer_id FROM games WHERE game_id=@id", new { id = game_id });
                if (owner != User.GetUserId())
                    return ApiResults.Error(403, "Access denied", "FORBIDDEN");
            }

            var (fileName, _, url) = await files.SaveGameImageAsync(game_id, file);
            await using var conn2 = db.CreateConnection();
            await conn2.OpenAsync();
            await conn2.ExecuteAsync("UPDATE games SET cover_url=@url WHERE game_id=@gid", new { url, gid = game_id });
            return Ok(new { url, fileName, size = file.Length, mimetype = file.ContentType, game_id });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }
}
