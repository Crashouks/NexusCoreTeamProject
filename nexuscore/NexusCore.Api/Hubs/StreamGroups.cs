namespace NexusCore.Api.Hubs
{
    public static class StreamGroups
    {
        public static string GetSessionGroup(string sessionId) => $"session_{sessionId}";
        public static string GetUserGroup(string userId) => $"user_{userId}";
    }
}
