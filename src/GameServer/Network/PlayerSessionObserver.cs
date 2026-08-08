using GameServer.Grains;

namespace GameServer.Network;

public sealed class PlayerSessionObserver : IPlayerSessionObserver
{
    private readonly ClientSession _session;

    public PlayerSessionObserver(ClientSession session) => _session = session;

    public Task DeliverAsync(byte[] packet) => _session.SendAsync(packet);
}
