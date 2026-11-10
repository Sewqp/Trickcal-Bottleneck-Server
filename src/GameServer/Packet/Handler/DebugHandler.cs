using GameServer.Config;
using GameServer.Network;

namespace GameServer.Packet.Handler;

// APM의 의도적 오류 사례는 명시적으로 켠 로컬 데모에서만 보존한다.
public static class DebugHandler
{
    public static async Task HandleGrantCurrencyAsync(ClientSession session, Memory<byte> packet)
    {
        if (!ServerConfig.Instance.EnableDebugCommands)
        {
            await session.SendAsync(PacketWriter.Build(PacketId.DebugGrantCurrencyResult, new byte[17]));
            return;
        }
        await ProgressHandler.HandleAsync(session, packet);
        if (!session.IsAdmin)
            throw new InvalidOperationException($"non-admin player {session.PlayerId} invoked admin-only DebugGrantCurrencyRequest");
    }
}
