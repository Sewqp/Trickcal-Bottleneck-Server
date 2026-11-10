using GameServer.DB;
using GameServer.DB.Model;
using GameServer.DB.Repository;
using GameServer.Network;

namespace GameServer.Packet.Handler;

public static class CharacterHandler
{
    private const int ListEntrySize = 4 + 1 + 4; // characterId + status + level

    public static async Task HandleListRequestAsync(ClientSession session, Memory<byte> packet)
    {
        var list = await CharacterRepository.Instance.GetStatusListAsync(session.PlayerId);
        await session.SendAsync(PacketWriter.Build(PacketId.CharacterListResult, BuildListPayload(list)));
    }

    // UI 진입 — 현재 레벨/마카롱·골드 잔량 전달(design-plan-draft.md 4장)
    public static async Task HandleEnhanceEnterAsync(ClientSession session, Memory<byte> packet)
    {
        if (packet.Length < PacketHeader.HeaderSize + 4) { Apm.Agent.LocalTelemetry.SetOutcome("rejected", "malformed_payload"); return; }
        int characterId = BitConverter.ToInt32(packet.Span[PacketHeader.HeaderSize..]);

        int level = await CharacterRepository.Instance.GetLevelAsync(session.PlayerId, characterId);
        var currency = await CurrencyRepository.Instance.GetAsync(session.PlayerId);

        // payload: [4B characterId][4B level][8B macaron][8B gold]
        var result = new byte[24];
        BitConverter.TryWriteBytes(result.AsSpan(0), characterId);
        BitConverter.TryWriteBytes(result.AsSpan(4), level);
        BitConverter.TryWriteBytes(result.AsSpan(8), currency.Macaron);
        BitConverter.TryWriteBytes(result.AsSpan(16), currency.Gold);
        await session.SendAsync(PacketWriter.Build(PacketId.CharacterEnhanceEnterResult, result));
    }

    public static Task HandleEnhanceExitAsync(ClientSession session, Memory<byte> packet) =>
        ProgressHandler.HandleAsync(session, packet);

    // payload: [2B count] + N * { [4B characterId][1B status][4B level] }
    private static byte[] BuildListPayload(List<CharacterStatusModel> list)
    {
        var buf = new byte[2 + list.Count * ListEntrySize];
        BitConverter.TryWriteBytes(buf.AsSpan(0), (ushort)list.Count);
        int offset = 2;
        foreach (var c in list)
        {
            BitConverter.TryWriteBytes(buf.AsSpan(offset), c.CharacterId); offset += 4;
            buf[offset] = c.Status; offset += 1;
            BitConverter.TryWriteBytes(buf.AsSpan(offset), c.Level); offset += 4;
        }
        return buf;
    }
}
