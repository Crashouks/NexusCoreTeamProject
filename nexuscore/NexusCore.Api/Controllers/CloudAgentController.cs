using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using NexusCore.Api.Helpers;
using NexusCore.Api.Hubs;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

[ApiController]
[Route("api/cloud/agent")]
[AllowAnonymous]
public class CloudAgentController(
    CloudServerService cloudServers,
    SessionExpiryService sessions,
    DbService db,
    CloudDiagnosticsLog diag,
    IHubContext<CloudStreamHub> streamHub) : ControllerBase
{
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] AgentAuthRequest body)
    {
        if (body.ServerId <= 0) return ApiResults.Error(400, "server_id is required", "VALIDATION_ERROR");
        var ok = await cloudServers.HeartbeatAsync(body.ServerId, body.Password);
        if (!ok)
        {
            diag.Warn("agent-api", "Heartbeat failed — invalid server id or agent password", $"server={body.ServerId}");
            return ApiResults.Error(401, "Invalid server id or password", "UNAUTHORIZED");
        }
        diag.Debug("agent-api", "Heartbeat OK", $"server={body.ServerId}");
        return Ok(new { message = "Heartbeat received", online = true });
    }

    [HttpPost("jobs/poll")]
    public async Task<IActionResult> PollJobs([FromBody] AgentAuthRequest body)
    {
        if (body.ServerId <= 0) return ApiResults.Error(400, "server_id is required", "VALIDATION_ERROR");
        if (!await cloudServers.VerifyAccessAsync(body.ServerId, body.Password))
        {
            diag.Warn("agent-api", "Job poll auth failed", $"server={body.ServerId}");
            return ApiResults.Error(401, "Invalid server id or password", "UNAUTHORIZED");
        }

        var jobs = await cloudServers.PollJobsAsync(body.ServerId, body.Password);
        if (jobs.Any())
            diag.Info("agent-api", "Jobs dispatched to agent", $"server={body.ServerId} count={jobs.Count()}");
        return Ok(new { jobs });
    }

    [HttpPost("jobs/{id:int}/status")]
    public async Task<IActionResult> UpdateJobStatus(int id, [FromBody] AgentJobStatusRequest body)
    {
        if (body.ServerId <= 0) return ApiResults.Error(400, "server_id is required", "VALIDATION_ERROR");
        var (ok, error) = await cloudServers.UpdateJobAsync(
            id, body.ServerId, body.Password, body.Status ?? "", body.Error);
        if (!ok && error == "Invalid server credentials")
            return ApiResults.Error(401, error, "UNAUTHORIZED");
        if (!ok) return ApiResults.Error(404, error ?? "Update failed", "NOT_FOUND");
        return Ok(new { message = "Job updated" });
    }

    [HttpPost("session-ended")]
    public async Task<IActionResult> SessionEnded([FromBody] AgentSessionEndedRequest body)
    {
        if (body.ServerId <= 0 || body.SessionId <= 0)
            return ApiResults.Error(400, "server_id and session_id are required", "VALIDATION_ERROR");

        var (ok, error, plan) = await cloudServers.EndSessionByAgentAsync(
            body.SessionId, body.ServerId, body.Password);
        if (!ok && error == "Invalid server credentials")
            return ApiResults.Error(401, error, "UNAUTHORIZED");
        if (!ok) return ApiResults.Error(404, error ?? "Session not found", "NOT_FOUND");

        await streamHub.Clients.Group($"stream-p-{body.SessionId}")
            .SendAsync("SessionEnded", new { reason = "game_closed" });

        if (plan == "free")
        {
            await using var conn = db.CreateConnection();
            await conn.OpenAsync();
            await sessions.PromoteQueueAsync(conn);
        }

        return Ok(new { message = "Session ended — game closed on host" });
    }

    public record AgentAuthRequest(int ServerId, string? Password);
    public record AgentJobStatusRequest(int ServerId, string? Password, string? Status, string? Error);
    public record AgentSessionEndedRequest(int ServerId, int SessionId, string? Password);
}
