using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using NexusCore.Api.Helpers;
using NexusCore.Api.Hubs;
using NexusCore.Api.Services;

namespace NexusCore.Api.Controllers;

/// <summary>
/// Endpoints called by the Windows cloud agent process (not by browser clients) to report
/// liveness, poll for launch/stop jobs, and report job/session status. Authenticated with a
/// per-server ID + password pair instead of the normal user JWT cookie.
/// </summary>
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
    /// <summary>
    /// Records a heartbeat from an agent, marking its server as online.
    /// </summary>
    /// <param name="body">Server ID and agent password to authenticate the heartbeat.</param>
    /// <response code="200">Heartbeat recorded.</response>
    /// <response code="400"><c>server_id</c> was missing or not positive.</response>
    /// <response code="401">The server ID/password pair is invalid.</response>
    [HttpPost("heartbeat")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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

    /// <summary>
    /// Polls for pending launch/stop jobs queued for this agent's server.
    /// </summary>
    /// <param name="body">Server ID and agent password to authenticate the poll.</param>
    /// <response code="200">Returns any pending jobs (an empty array if none).</response>
    /// <response code="400"><c>server_id</c> was missing or not positive.</response>
    /// <response code="401">The server ID/password pair is invalid.</response>
    [HttpPost("jobs/poll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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

    /// <summary>
    /// Reports the outcome of a job the agent previously polled for.
    /// </summary>
    /// <param name="id">Route parameter: the job ID being updated.</param>
    /// <param name="body">Server ID, agent password, new status, and optional error detail.</param>
    /// <response code="200">Job status updated.</response>
    /// <response code="400"><c>server_id</c> was missing or not positive.</response>
    /// <response code="401">The server ID/password pair is invalid.</response>
    /// <response code="404">No matching job was found, or the update otherwise failed.</response>
    [HttpPost("jobs/{id:int}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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

    /// <summary>
    /// Reports that a cloud session's game process has closed on the host machine.
    /// </summary>
    /// <remarks>
    /// Notifies any connected spectators/viewers over SignalR that the session ended, and
    /// promotes the next person in the free-tier queue if the session was on the free plan.
    /// </remarks>
    /// <param name="body">Server ID, session ID, and agent password to authenticate the report.</param>
    /// <response code="200">Session marked as ended.</response>
    /// <response code="400"><c>server_id</c> or <c>session_id</c> was missing or not positive.</response>
    /// <response code="401">The server ID/password pair is invalid.</response>
    /// <response code="404">No matching session was found.</response>
    [HttpPost("session-ended")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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

    /// <summary>Server ID + agent password pair shared by all agent-authenticated requests.</summary>
    public record AgentAuthRequest(int ServerId, string? Password);

    /// <summary>Request body for <see cref="UpdateJobStatus"/>.</summary>
    /// <param name="ServerId">The reporting server's ID.</param>
    /// <param name="Password">The server's agent password.</param>
    /// <param name="Status">New job status.</param>
    /// <param name="Error">Optional error detail if the job failed.</param>
    public record AgentJobStatusRequest(int ServerId, string? Password, string? Status, string? Error);

    /// <summary>Request body for <see cref="SessionEnded"/>.</summary>
    /// <param name="ServerId">The reporting server's ID.</param>
    /// <param name="SessionId">The cloud session that ended.</param>
    /// <param name="Password">The server's agent password.</param>
    public record AgentSessionEndedRequest(int ServerId, int SessionId, string? Password);
}