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

    // UI 진입 — 현재 레벨/마카롱 잔량 전달(design-plan-draft.md 4장)
    public static async Task HandleEnhanceEnterAsync(ClientSession session, Memory<byte> packet)
    {
        if (packet.Length < PacketHeader.HeaderSize + 4) return;
        int characterId = BitConverter.ToInt32(packet.Span[PacketHeader.HeaderSize..]);

        int level = await CharacterRepository.Instance.GetLevelAsync(session.PlayerId, characterId);
        var currency = await CurrencyRepository.Instance.GetAsync(session.PlayerId);

        // payload: [4B characterId][4B level][8B macaron]
        var result = new byte[16];
        BitConverter.TryWriteBytes(result.AsSpan(0), characterId);
        BitConverter.TryWriteBytes(result.AsSpan(4), level);
        BitConverter.TryWriteBytes(result.AsSpan(8), currency.Macaron);
        await session.SendAsync(PacketWriter.Build(PacketId.CharacterEnhanceEnterResult, result));
    }

    // UI 이탈 — 클라가 보낸 "M→N"은 트리거일 뿐, 실제 차감은 항상 서버가 고정 공식으로 재계산한 값 기준
    public static async Task HandleEnhanceExitAsync(ClientSession session, Memory<byte> packet)
    {
        if (packet.Length < PacketHeader.HeaderSize + 8) return;
        var p = packet.Span[PacketHeader.HeaderSize..];
        int characterId = BitConverter.ToInt32(p);
        int reportedLevel = BitConverter.ToInt32(p[4..]);

        int currentLevel = await CharacterRepository.Instance.GetLevelAsync(session.PlayerId, characterId);
        bool success = false;

        if (reportedLevel > currentLevel)
        {
            long cost = CalculateEnhanceCost(currentLevel, reportedLevel);
            success = await CurrencyRepository.Instance.TrySpendMacaronAsync(session.PlayerId, cost);
            if (success)
            {
                await CharacterRepository.Instance.SetLevelAsync(session.PlayerId, characterId, reportedLevel);

                // stat_growth_table은 "레벨업 1회당 증가량" 고정값 — 오른 레벨 수만큼 곱해 스탯 델타를
                // 구하고, 공식이 선형이라 그 델타 하나만 전투력으로 환산해도 전체 재계산과 동일함
                var info = await CharacterRepository.Instance.GetCharacterInfoAsync(characterId);
                if (info is not null)
                {
                    var growth = CombatPowerCalculator.StatVector.Parse(info.StatGrowthTableJson);
                    var delta = growth * (reportedLevel - currentLevel);
                    await PlayerStatRepository.Instance.IncrementCombatPowerAsync(
                        session.PlayerId, CombatPowerCalculator.FromStats(delta));
                }
            }
        }

        int finalLevel = success ? reportedLevel : currentLevel;
        var currency = await CurrencyRepository.Instance.GetAsync(session.PlayerId);

        // payload: [1B success][4B level][8B macaron]
        var result = new byte[13];
        result[0] = (byte)(success ? 1 : 0);
        BitConverter.TryWriteBytes(result.AsSpan(1), finalLevel);
        BitConverter.TryWriteBytes(result.AsSpan(5), currency.Macaron);
        await session.SendAsync(PacketWriter.Build(PacketId.CharacterEnhanceExitResult, result));
    }

    // 실 밸런스 데이터가 없어 임시 선형 공식(레벨 하나 올릴 때마다 10 * 목표레벨) — 나중에
    // character_info.stat_growth_table 기반 실제 테이블로 교체 가능하게 이 함수 하나로 고립시켜둠
    private static long CalculateEnhanceCost(int fromLevel, int toLevel)
    {
        long cost = 0;
        for (int level = fromLevel + 1; level <= toLevel; level++)
            cost += 10L * level;
        return cost;
    }

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
