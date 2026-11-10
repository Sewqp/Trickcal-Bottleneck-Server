using GameServer.Grains;
using GameServer.Network;

namespace GameServer.Packet.Handler;

public static class ProgressHandler
{
    public static async Task HandleAsync(ClientSession session, Memory<byte> packet)
    {
        var id = BitConverter.ToUInt16(packet.Span[2..]);
        var response = await OrleansClient.Instance.Factory.GetGrain<IPlayerGrain>(session.PlayerId)
            .ExecuteProgressAsync(session.SessionId, id, packet[PacketHeader.HeaderSize..].ToArray());
        if (response.Length > 0) await session.SendAsync(response);
    }
}
