using GameServer.Grains;
using GameServer.Network;

namespace GameServer.Packet.Handler;

public static class MatchHandler
{
    public static Task HandleAsync(ClientSession session, Memory<byte> packet)
        => OrleansClient.Instance.Factory.GetGrain<IMatchGrain>(0).RequestMatchAsync(session.PlayerId);
}
