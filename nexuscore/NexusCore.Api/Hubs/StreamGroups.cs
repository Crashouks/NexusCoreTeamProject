namespace NexusCore.Api.Hubs;

public static class StreamGroups
{
    public static string Player(int sessionId) => $"stream-p-{sessionId}";
    public static string Agent(int sessionId) => $"stream-a-{sessionId}";
    public static string Spectator(int sessionId) => $"stream-s-{sessionId}";
}
