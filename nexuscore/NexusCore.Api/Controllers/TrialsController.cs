using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusCore.Api.Extensions;
using NexusCore.Api.Helpers;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/trials")]
[Authorize]
public class TrialsController(DbService db) : ControllerBase
{
    [HttpGet("status/{gameId:int}")]
    public async Task<IActionResult> Status(int gameId)
    {
        try
        {
            var game = await GetGameTrialConfigAsync(gameId);
            if (game == null) return ApiResults.Error(404, "Game not found", "NOT_FOUND");
            var duration = game.trial_duration_mins != null ? (int)game.trial_duration_mins : 30;
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var trials = (await conn.QueryAsync<dynamic>(
                "SELECT * FROM trials WHERE user_id=@uid AND game_id=@gid",
                new { uid = User.GetUserId(), gid = gameId })).ToList();
            if (trials.Count == 0)
            {
                return Ok(new
                {
                    canTrial = DbValue.IsTrue(game.trial_enabled),
                    trialUsed = false, trialStatus = (string?)null,
                    minutesRemaining = duration, trialDuration = duration,
                    trialDiscount = game.trial_discount_percent
                });
            }
            var t = trials[0];
            var status = (string)t.status;
            var trialUsed = status is "completed" or "purchased" or "expired";
            var minutesRemaining = 0;
            if (status == "active")
                minutesRemaining = Math.Max(0, duration - (int)t.duration_mins);
            return Ok(new
            {
                canTrial = !trialUsed && status != "active" && DbValue.IsTrue(game.trial_enabled),
                trialUsed, trialStatus = status, trialId = (int)t.trial_id,
                minutesRemaining, startedAt = t.started_at, trialDuration = duration,
                progressPercent = Math.Min(100, (int)Math.Round((double)(int)t.duration_mins / duration * 100)),
                trialDiscount = game.trial_discount_percent
            });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("start/{gameId:int}")]
    public async Task<IActionResult> Start(int gameId)
    {
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var userId = User.GetUserId();
            var game = await GetGameTrialConfigAsync(gameId);
            if (game == null) return ApiResults.Error(404, "Game not found", "NOT_FOUND");
            if (!DbValue.IsTrue(game.trial_enabled))
                return ApiResults.Error(403, "Trial not available", "TRIAL_DISABLED");

            var duration = game.trial_duration_mins != null ? (int)game.trial_duration_mins : 30;
            var existing = (await conn.QueryAsync<dynamic>(
                "SELECT * FROM trials WHERE user_id=@uid AND game_id=@gid", new { uid = userId, gid = gameId }, tx)).ToList();

            if (existing.Count > 0)
            {
                var status = (string)existing[0].status;
                if (status == "active")
                {
                    await tx.CommitAsync();
                    return Ok(new
                    {
                        trialId = (int)existing[0].trial_id,
                        expiresAt = ((DateTime)existing[0].started_at).AddMinutes(duration),
                        cloudSessionId = (long?)null,
                        cloudEnabled = DbValue.IsTrue(game.cloud_enabled),
                        trialDuration = duration,
                        resumed = true,
                        game = new
                        {
                            name = (string)game.name,
                            cover_url = game.cover_url,
                            price = game.price,
                            trial_discount_percent = game.trial_discount_percent
                        }
                    });
                }
                if (status is "completed" or "purchased" or "expired")
                    return ApiResults.Error(409, "Trial already used", "TRIAL_ALREADY_USED");
            }

            var owned = await conn.ExecuteScalarAsync<int?>(
                "SELECT 1 FROM libraries WHERE user_id=@uid AND game_id=@gid", new { uid = userId, gid = gameId }, tx);
            if (owned != null) return ApiResults.Error(409, "Already owned", "ALREADY_OWNED");

            var trialId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO trials (user_id, game_id, status) VALUES (@uid, @gid, 'active'); SELECT LAST_INSERT_ID();",
                new { uid = userId, gid = gameId }, tx);

            long? cloudSessionId = null;
            if (DbValue.IsTrue(game.cloud_enabled))
            {
                var plan = await conn.ExecuteScalarAsync<string>(
                    "SELECT cloud_plan FROM users WHERE user_id=@uid", new { uid = userId }, tx) ?? "free";
                if (plan == "none") plan = "free";
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                cloudSessionId = await conn.ExecuteScalarAsync<long>(
                    @"INSERT INTO cloud_sessions (user_id, game_id, plan, max_duration_mins, stream_token, status)
                      VALUES (@uid, @gid, @plan, @dur, @token, 'active');
                      SELECT LAST_INSERT_ID();",
                    new { uid = userId, gid = gameId, plan = plan == "free" ? "free" : plan, dur = duration, token }, tx);
            }

            await tx.CommitAsync();
            return StatusCode(201, new
            {
                trialId,
                expiresAt = DateTime.UtcNow.AddMinutes(duration),
                cloudSessionId,
                cloudEnabled = DbValue.IsTrue(game.cloud_enabled),
                trialDuration = duration,
                game = new
                {
                    name = (string)game.name,
                    cover_url = game.cover_url,
                    price = game.price,
                    trial_discount_percent = game.trial_discount_percent
                }
            });
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1062)
        {
            await tx.RollbackAsync();
            return ApiResults.Error(409, "Trial already used", "TRIAL_ALREADY_USED");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return ApiResults.Error(500, ex.Message, "SERVER_ERROR");
        }
    }

    [HttpPost("end/{trialId:int}")]
    public async Task<IActionResult> End(int trialId)
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var trials = (await conn.QueryAsync<dynamic>(
                @"SELECT t.*, g.trial_duration_mins, g.trial_discount_percent, g.name, g.cover_url, g.price
                  FROM trials t JOIN games g ON t.game_id=g.game_id
                  WHERE t.trial_id=@id AND t.user_id=@uid",
                new { id = trialId, uid = User.GetUserId() })).ToList();
            if (trials.Count == 0) return ApiResults.Error(404, "Trial not found", "NOT_FOUND");
            var t = trials[0];
            var duration = t.trial_duration_mins != null ? (int)t.trial_duration_mins : 30;
            if ((string)t.status != "active")
                return Ok(new { message = "Trial already ended", status = (string)t.status });

            var mins = Math.Min(await conn.ExecuteScalarAsync<int>(
                "SELECT TIMESTAMPDIFF(MINUTE, @start, NOW())", new { start = t.started_at }), duration);
            await conn.ExecuteAsync(
                "UPDATE trials SET status='completed', ended_at=NOW(), duration_mins=@mins WHERE trial_id=@id",
                new { mins, id = trialId });
            await conn.ExecuteAsync(
                "UPDATE cloud_sessions SET status='ended', ended_at=NOW() WHERE user_id=@uid AND game_id=@gid AND status='active'",
                new { uid = (int)t.user_id, gid = (int)t.game_id });

            return Ok(new
            {
                message = "Trial ended", durationMins = mins, trialExpired = mins >= duration,
                game = new
                {
                    name = (string)t.name, cover_url = t.cover_url, price = t.price,
                    trial_discount_percent = t.trial_discount_percent, game_id = (int)t.game_id
                }
            });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpPost("heartbeat/{trialId:int}")]
    public async Task<IActionResult> Heartbeat(int trialId)
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var trials = (await conn.QueryAsync<dynamic>(
                @"SELECT t.*, g.trial_duration_mins, g.trial_discount_percent, g.name, g.cover_url, g.price
                  FROM trials t JOIN games g ON t.game_id=g.game_id
                  WHERE t.trial_id=@id AND t.user_id=@uid",
                new { id = trialId, uid = User.GetUserId() })).ToList();
            if (trials.Count == 0) return ApiResults.Error(404, "Trial not found", "NOT_FOUND");
            var t = trials[0];
            var duration = t.trial_duration_mins != null ? (int)t.trial_duration_mins : 30;
            if ((string)t.status != "active") return Ok(new { trialExpired = true, status = (string)t.status });

            var mins = await conn.ExecuteScalarAsync<int>(
                "SELECT TIMESTAMPDIFF(MINUTE, @start, NOW())", new { start = t.started_at });
            await conn.ExecuteAsync("UPDATE trials SET duration_mins=@mins WHERE trial_id=@id", new { mins, id = trialId });

            if (mins >= duration)
            {
                await conn.ExecuteAsync(
                    "UPDATE trials SET status='completed', ended_at=NOW(), duration_mins=@dur WHERE trial_id=@id",
                    new { dur = duration, id = trialId });
                await conn.ExecuteAsync(
                    "UPDATE cloud_sessions SET status='ended', ended_at=NOW() WHERE user_id=@uid AND game_id=@gid AND status='active'",
                    new { uid = (int)t.user_id, gid = (int)t.game_id });
                return Ok(new
                {
                    trialExpired = true,
                    game = new
                    {
                        name = (string)t.name, cover_url = t.cover_url, price = t.price,
                        trial_discount_percent = t.trial_discount_percent, game_id = (int)t.game_id
                    }
                });
            }
            return Ok(new
            {
                trialExpired = false,
                minutesRemaining = duration - mins,
                progressPercent = (int)Math.Round((double)mins / duration * 100)
            });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("history")]
    public async Task<IActionResult> History()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = (await conn.QueryAsync<dynamic>(
                @"SELECT t.*, g.name, g.cover_url, g.price, g.slug, g.trial_duration_mins, g.trial_discount_percent
                  FROM trials t JOIN games g ON t.game_id=g.game_id
                  WHERE t.user_id=@uid ORDER BY t.started_at DESC",
                new { uid = User.GetUserId() })).ToList();
            return Ok(rows.Select(r =>
            {
                var dict = PricingService.RowToDict(r);
                var td = r.trial_duration_mins != null ? (int)r.trial_duration_mins : 0;
                dict["progressPercent"] = td > 0
                    ? Math.Min(100, (int)Math.Round((double)(int)r.duration_mins / td * 100))
                    : 0;
                return dict;
            }));
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("active")]
    public async Task<IActionResult> Active()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = (await conn.QueryAsync<dynamic>(
                @"SELECT t.*, g.name, g.cover_url, g.price, g.slug, g.trial_duration_mins, g.trial_discount_percent
                  FROM trials t JOIN games g ON t.game_id=g.game_id
                  WHERE t.user_id=@uid AND t.status='active'",
                new { uid = User.GetUserId() })).ToList();
            return Ok(rows.Select(r =>
            {
                var dict = PricingService.RowToDict(r);
                var td = r.trial_duration_mins != null ? (int)r.trial_duration_mins : 30;
                dict["minutesRemaining"] = Math.Max(0, td - (int)r.duration_mins);
                dict["progressPercent"] = Math.Min(100, (int)Math.Round((double)(int)r.duration_mins / td * 100));
                return dict;
            }));
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    [HttpGet("all")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> All()
    {
        try
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            var rows = await conn.QueryAsync(
                @"SELECT t.*, u.username, g.name AS game_name FROM trials t
                  JOIN users u ON t.user_id=u.user_id JOIN games g ON t.game_id=g.game_id
                  ORDER BY t.started_at DESC LIMIT 200");
            var stats = await conn.QuerySingleAsync(
                @"SELECT COUNT(*) AS total, SUM(status='completed') AS completed,
                         SUM(status='purchased') AS purchased, SUM(status='active') AS active FROM trials");
            return Ok(new { trials = rows, stats });
        }
        catch (Exception ex) { return ApiResults.Error(500, ex.Message, "SERVER_ERROR"); }
    }

    private async Task<dynamic?> GetGameTrialConfigAsync(int gameId)
    {
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var games = (await conn.QueryAsync<dynamic>(
            @"SELECT trial_enabled, trial_duration_mins, trial_discount_percent, trial_level_limit,
                     cloud_enabled, name, cover_url, price FROM games WHERE game_id=@id",
            new { id = gameId })).ToList();
        return games.Count > 0 ? games[0] : null;
    }
}
