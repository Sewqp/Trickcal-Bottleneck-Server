using Orleans;
using Orleans.Concurrency;

namespace GameServer.Grains;

public interface IPlayerSessionObserver : IGrainObserver
{
    [OneWay]
    Task DeliverAsync(byte[] packet);
}
