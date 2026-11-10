using GameServer.Network;
namespace GameServer.Packet.Handler;
public static class TheaterHandler
{
    public static Task HandleEnterAsync(ClientSession session, Memory<byte> packet) => ProgressHandler.HandleAsync(session, packet);
    public static Task HandleExitAsync(ClientSession session, Memory<byte> packet) => ProgressHandler.HandleAsync(session, packet);
    public static Task HandleTouchAsync(ClientSession session, Memory<byte> packet) => ProgressHandler.HandleAsync(session, packet);
}
