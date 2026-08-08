using Orleans;

namespace GameServer.Grains;

public interface IMatchGrain : IGrainWithIntegerKey
{
    Task RequestMatchAsync(long playerId);
    Task CancelMatchAsync(long playerId);
}
