using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/downloads")]
[Authorize]
public class DownloadsController(DbService db, NotificationService notifications) : ControllerBase
{
  public static double DownloadDurationSeconds(decimal sizeGb) =>
      Math.Max(0.1, (double)sizeGb / 10.0);

  [HttpGet]
  public async Task<IActionResult> List()
  {
    try
    {
      var uid = User.GetUserId();
      await using var conn = db.CreateConnection();
      await conn.OpenAsync();
      var rows = await conn.QueryAsync(@"
        SELECT g.game_id, g.name, g.slug, g.cover_url, g.genre, g.cloud_enabled,
               COALESCE(g.download_size_gb, 25) AS download_size_gb,
               l.download_status, l.download_progress, l.purchase_date
        FROM libraries l
        JOIN games g ON l.game_id = g.game_id
        WHERE l.user_id = @uid
        ORDER BY
          CASE l.download_status WHEN 'downloading' THEN 0 WHEN 'none' THEN 1 ELSE 2 END,
          l.purchase_date DESC", new { uid });
      return Ok(rows);
    }
    catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
  }

  [HttpPost("{gameId:int}/start")]
  public async Task<IActionResult> Start(int gameId)
  {
    try
    {
      var uid = User.GetUserId();
      await using var conn = db.CreateConnection();
      await conn.OpenAsync();
      var row = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
        SELECT l.*, COALESCE(g.download_size_gb, 25) AS download_size_gb
        FROM libraries l JOIN games g ON l.game_id = g.game_id
        WHERE l.user_id = @uid AND l.game_id = @gid", new { uid, gid = gameId });
      if (row == null) return ApiResults.Error(404, "Game not in library", "NOT_FOUND");
      if ((string)row.download_status == "installed")
        return ApiResults.Error(409, "Already installed", "ALREADY_INSTALLED");

      await conn.ExecuteAsync(
          "UPDATE libraries SET download_status='downloading', download_progress=0 WHERE user_id=@uid AND game_id=@gid",
          new { uid, gid = gameId });

      var sizeGb = Convert.ToDecimal(row.download_size_gb);
      return Ok(new
      {
          game_id = gameId,
          download_status = "downloading",
          download_progress = 0,
          download_size_gb = sizeGb,
          duration_seconds = DownloadDurationSeconds(sizeGb)
      });
    }
    catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
  }

  [HttpPost("{gameId:int}/progress")]
  public async Task<IActionResult> Progress(int gameId, [FromBody] DownloadProgressRequest body)
  {
    try
    {
      var uid = User.GetUserId();
      var progress = Math.Clamp(body.Progress, 0, 100);
      await using var conn = db.CreateConnection();
      await conn.OpenAsync();
      var updated = await conn.ExecuteAsync(@"
        UPDATE libraries SET download_progress=@p
        WHERE user_id=@uid AND game_id=@gid AND download_status='downloading'",
          new { uid, gid = gameId, p = progress });
      if (updated == 0) return ApiResults.Error(404, "No active download", "NOT_FOUND");
      return Ok(new { download_progress = progress });
    }
    catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
  }

  [HttpPost("{gameId:int}/complete")]
  public async Task<IActionResult> Complete(int gameId)
  {
    try
    {
      var uid = User.GetUserId();
      await using var conn = db.CreateConnection();
      await conn.OpenAsync();
      var game = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
        SELECT g.name, g.slug FROM libraries l JOIN games g ON l.game_id = g.game_id
        WHERE l.user_id=@uid AND l.game_id=@gid", new { uid, gid = gameId });
      var updated = await conn.ExecuteAsync(@"
        UPDATE libraries SET download_status='installed', download_progress=100
        WHERE user_id=@uid AND game_id=@gid",
          new { uid, gid = gameId });
      if (updated == 0) return ApiResults.Error(404, "Game not in library", "NOT_FOUND");
      if (game != null)
      {
          var name = (string?)game.name ?? "a game";
          var slug = (string?)game.slug;
          await notifications.CreateAsync(uid, "download",
              $"Download complete — {name} is ready to play",
              slug != null ? $"/games/{slug}" : "/library?tab=installed",
              refGameId: gameId);
      }
      return Ok(new { download_status = "installed", download_progress = 100 });
    }
    catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
  }

  public record DownloadProgressRequest(int Progress);
}
