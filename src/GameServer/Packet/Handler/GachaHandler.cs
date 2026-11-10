using GameServer.Network;
namespace GameServer.Packet.Handler;
public static class GachaHandler
{
    public static Task HandleAsync(ClientSession session, Memory<byte> packet) => ProgressHandler.HandleAsync(session, packet);
}
