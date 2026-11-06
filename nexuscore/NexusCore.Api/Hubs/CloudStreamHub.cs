using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace NexusCore.Api.Hubs
{
    [Authorize]
    public class CloudStreamHub : Hub
    {
        private readonly ILogger<CloudStreamHub> _logger;

        public CloudStreamHub(ILogger<CloudStreamHub> logger)
        {
            _logger = logger;
        }

        public override async Task OnConnectedAsync()
        {
            var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? Context.User?.Identity?.Name;
            _logger.LogInformation("Client connected: {ConnectionId}, User: {UserId}", Context.ConnectionId, userId);
            
            if (!string.IsNullOrEmpty(userId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.GetUserGroup(userId));
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (exception != null)
            {
                _logger.LogWarning(exception, "Client disconnected with error: {ConnectionId}, User: {UserId}", Context.ConnectionId, userId);
            }
            else
            {
                _logger.LogInformation("Client disconnected gracefully: {ConnectionId}, User: {UserId}", Context.ConnectionId, userId);
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task JoinSession(string sessionId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, StreamGroups.GetSessionGroup(sessionId));
            _logger.LogInformation("Connection {ConnectionId} joined session group {SessionId}", Context.ConnectionId, sessionId);
        }

        public async Task LeaveSession(string sessionId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, StreamGroups.GetSessionGroup(sessionId));
            _logger.LogInformation("Connection {ConnectionId} left session group {SessionId}", Context.ConnectionId, sessionId);
        }

        public async Task SendSessionTelemetry(string sessionId, string status, int latencyMs)
        {
            _logger.LogDebug("Session {SessionId} status updated to {Status}, latency {Latency}ms", sessionId, status, latencyMs);
            
            await Clients.Group(StreamGroups.GetSessionGroup(sessionId)).SendAsync("ReceiveSessionUpdate", new
            {
                SessionId = sessionId,
                Status = status,
                LatencyMs = latencyMs,
                Timestamp = DateTime.UtcNow
            });
        }
    }
}
