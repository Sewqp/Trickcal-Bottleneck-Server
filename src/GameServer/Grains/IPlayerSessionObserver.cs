using Orleans;
using Orleans.Concurrency;

namespace GameServer.Grains;

public interface IPlayerSessionObserver : IGrainObserver
{
    [OneWay]
    Task DeliverAsync(byte[] packet);

    [OneWay]
    Task DeliverMatchResultAsync(byte[] packet, string? requestTraceParent);
}
