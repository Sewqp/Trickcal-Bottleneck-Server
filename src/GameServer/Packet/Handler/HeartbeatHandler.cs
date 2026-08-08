using GameServer.Network;

namespace GameServer.Packet.Handler;

public static class HeartbeatHandler
{
    public static async Task HandleAsync(ClientSession session, Memory<byte> packet)
    {
        session.UpdateLastReceived();
        await session.SendAsync(PacketWriter.Build(PacketId.Heartbeat));
    }
}
