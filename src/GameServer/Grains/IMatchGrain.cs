using Orleans;

namespace GameServer.Grains;

public enum MatchRequestStatus
{
    Queued,
    AlreadyQueued,
    Created,
    StaleSession,
    NotificationUnavailable
}

public interface IMatchGrain : IGrainWithIntegerKey
{
    Task<MatchRequestStatus> RequestMatchAsync(long playerId, Guid sessionId, string? requestTraceParent);
    Task CancelMatchAsync(long playerId, Guid sessionId);
}
