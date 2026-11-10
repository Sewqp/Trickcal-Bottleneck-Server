using GameServer.DB;
using GameServer.DB.Model;
using GameServer.DB.Repository;
using GameServer.Network;

namespace GameServer.Packet.Handler;

public static class BoardHandler
{
    public static async Task HandleListRequestAsync(ClientSession session, Memory<byte> packet)
    {
        if (packet.Length < PacketHeader.HeaderSize + 4) { Apm.Agent.LocalTelemetry.SetOutcome("rejected", "malformed_payload"); return; }
        int characterId = BitConverter.ToInt32(packet.Span[PacketHeader.HeaderSize..]);

        var personal = await BoardRepository.Instance.GetPlayerCharacterBoardsAsync(session.PlayerId, characterId);
        // global_board.character_id는 효과 대상이 아니라 "어느 캐릭터 시퀀스에 배치되는지"일 뿐이라,
        // 이 캐릭터 화면에 보여줄 것만 걸러냄(효과 자체는 계정 전체에 이미 적용돼 있음)
        var global = (await BoardRepository.Instance.GetPlayerGlobalBoardsAsync(session.PlayerId))
            .Where(b => b.CharacterId == characterId)
            .ToList();

        await session.SendAsync(PacketWriter.Build(PacketId.BoardListResult, BuildListPayload(personal, global)));
    }

    public static Task HandleUnlockRequestAsync(ClientSession session, Memory<byte> packet) =>
        ProgressHandler.HandleAsync(session, packet);

    // payload: [2B personalCount] + N*{4B boardNo,1B status} + [2B globalCount] + N*{4B boardNo,1B status}
    private static byte[] BuildListPayload(List<PlayerCharacterBoardModel> personal, List<PlayerGlobalBoardModel> global)
    {
        int size = 2 + personal.Count * 5 + 2 + global.Count * 5;
        var buf = new byte[size];
        int offset = 0;

        BitConverter.TryWriteBytes(buf.AsSpan(offset), (ushort)personal.Count); offset += 2;
        foreach (var b in personal)
        {
            BitConverter.TryWriteBytes(buf.AsSpan(offset), b.BoardNo); offset += 4;
            buf[offset] = b.Status; offset += 1;
        }

        BitConverter.TryWriteBytes(buf.AsSpan(offset), (ushort)global.Count); offset += 2;
        foreach (var b in global)
        {
            BitConverter.TryWriteBytes(buf.AsSpan(offset), b.BoardNo); offset += 4;
            buf[offset] = b.Status; offset += 1;
        }

        return buf;
    }
}
