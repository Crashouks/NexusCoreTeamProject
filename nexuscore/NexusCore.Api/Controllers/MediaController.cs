using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

/// <summary>
/// Handles image uploads for games and user avatars, serving stored game images, and
/// validating externally-hosted image URLs.
/// </summary>
[ApiController]
[Route("api/media")]
public class MediaController(DbService db, MediaFileService files) : ControllerBase
{
    private static readonly string[] AllowedTypes = ["image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif"];
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// Uploads an image for a game and sets it as the game's cover image.
    /// </summary>
    /// <remarks>Only the game's developer or an admin may upload media for it.</remarks>
    /// <param name="file">The image file to upload (multipart/form-data). Allowed types: JPEG, PNG, WebP, GIF.</param>
    /// <param name="game_id">Form field: the ID of the game the image belongs to.</param>
    /// <param name="media_type">Form field: optional media type label (defaults to "image").</param>
    /// <response code="200">Upload succeeded; returns the stored file's URL, name, size, and MIME type.</response>
    /// <response code="400">No file was provided, <c>game_id</c> was missing, or the file type is not an allowed image type.</response>
    /// <response code="403">The caller is not the game's developer and not an admin.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost("upload")]
    [Authorize(Roles = "developer,admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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
    /// <param name="gameId">Route parameter: the ID of the game whose cover image is requested.</param>
    /// <response code="200">Returns the raw image file.</response>
    /// <response code="404">No stored image exists for this game.</response>
    [HttpGet("game/{gameId:int}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetGameImage(int gameId)
    {
        var path = files.FindGameImagePath(gameId);
        if (path == null) return ApiResults.Error(404, "File not found", "NOT_FOUND");
        if (!ContentTypes.TryGetContentType(path, out var contentType))
            contentType = "application/octet-stream";
        return PhysicalFile(path, contentType, Path.GetFileName(path));
    }

    /// <summary>
    /// Validates that a remote URL points to an accessible image, without downloading it.
    /// </summary>
    /// <remarks>Sends an HTTP HEAD request to the URL and inspects the response's <c>Content-Type</c>.</remarks>
    /// <param name="url">Query parameter: the remote image URL to validate.</param>
    /// <response code="200">The URL is reachable and points to an image; returns its content type.</response>
    /// <response code="400">The URL was missing, unreachable, or does not point to an image.</response>
    [HttpGet("validate-url")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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

    /// <summary>
    /// Uploads a new avatar image for the caller's own account.
    /// </summary>
    /// <param name="file">The image file to upload (multipart/form-data). Allowed types: JPEG, PNG, WebP, GIF. Max 10 MB.</param>
    /// <response code="200">Upload succeeded; returns the new avatar URL, size, and MIME type.</response>
    /// <response code="400">No file was provided, the file type is not allowed, or the file exceeds 10 MB.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost("avatar")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

/// <summary>
/// Legacy/alternate game-image upload endpoint. Prefer <see cref="MediaController.Upload"/>;
/// this exists for backward compatibility with older clients calling <c>/api/upload</c>.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public class UploadController(DbService db, MediaFileService files) : ControllerBase
{
    private static readonly string[] AllowedTypes = ["image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif"];

    /// <summary>
    /// Uploads an image for a game and sets it as the game's cover image.
    /// </summary>
    /// <remarks>Only the game's developer or an admin may upload media for it.</remarks>
    /// <param name="file">The image file to upload (multipart/form-data). Allowed types: JPEG, PNG, WebP, GIF.</param>
    /// <param name="game_id">Form field: the ID of the game the image belongs to.</param>
    /// <response code="200">Upload succeeded; returns the stored file's URL, name, size, and MIME type.</response>
    /// <response code="400">No file was provided, <c>game_id</c> was missing, or the file type is not an allowed image type.</response>
    /// <response code="403">The caller is not the game's developer and not an admin.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost("upload")]
    [Authorize(Roles = "developer,admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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