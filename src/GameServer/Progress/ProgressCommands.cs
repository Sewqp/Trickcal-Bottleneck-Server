using GameServer.Config;
using GameServer.DB;
using GameServer.DB.Repository;
using GameServer.Packet;

namespace GameServer.Progress;

public sealed class ProgressCommands(long playerId, ProgressState state)
{
    public async Task<byte[]> UnlockBoardAsync(byte[] payload)
    {
        if (payload.Length != 9 || payload[8] > 1) return BoardResult(false);
        int characterId = BitConverter.ToInt32(payload);
        string field = ProgressState.BoardField(characterId, BitConverter.ToInt32(payload, 4));
        bool global = payload[8] == 1;
        var masters = global ? state.GlobalBoards : state.PersonalBoards;
        var unlocked = global ? state.UnlockedGlobal : state.UnlockedPersonal;
        if (!state.Levels.ContainsKey(characterId) || unlocked.Contains(field) ||
            !masters.TryGetValue(field, out var board) || board.Cost < 0) return BoardResult(false);
        bool success = await ProgressRepository.UnlockAsync(playerId, field, global, board.Cost, state.BoardDelta(board, global));
        if (success) unlocked.Add(field);
        return BoardResult(success);
    }

    public async Task<byte[]> EnhanceAsync(byte[] payload)
    {
        int characterId = payload.Length == 8 ? BitConverter.ToInt32(payload) : 0;
        int target = payload.Length == 8 ? BitConverter.ToInt32(payload, 4) : 0;
        bool owned = state.Levels.TryGetValue(characterId, out var current);
        long macaron = 0, gold = 0;
        bool success = false;
        if (owned && target > current && target <= ServerConfig.Instance.MaxCharacterLevel)
        {
            // 조작된 목표 레벨 때문에 긴 반복문을 돌지 않도록 합계 공식을 사용한다.
            long levels = checked(((long)target * (target + 1L) - (long)current * (current + 1L)) / 2);
            macaron = checked(10 * levels);
            gold = checked(20 * levels);
            success = await ProgressRepository.EnhanceAsync(playerId, characterId, current, target,
                gold, macaron, state.EnhancementDelta(characterId, target));
            if (success) state.Levels[characterId] = current = target;
        }
        var currency = await CurrencyRepository.Instance.GetAsync(playerId);
        var result = new byte[37];
        result[0] = success ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(result.AsSpan(1), owned ? current : 0);
        BitConverter.TryWriteBytes(result.AsSpan(5), currency.Macaron);
        BitConverter.TryWriteBytes(result.AsSpan(13), currency.Gold);
        BitConverter.TryWriteBytes(result.AsSpan(21), success ? macaron : 0);
        BitConverter.TryWriteBytes(result.AsSpan(29), success ? gold : 0);
        return PacketWriter.Build(PacketId.CharacterEnhanceExitResult, result);
    }

    public async Task<byte[]> GachaAsync(byte[] payload)
    {
        // 기존 빈 payload도 지원. 재전송을 구별하는 클라이언트는 16바이트 UUID를 보낸다.
        if (payload.Length is not (0 or 16) || state.Characters.Count == 0) return GachaFailure();
        var requestId = payload.Length == 16 ? new Guid(payload) : Guid.NewGuid();
        var cached = await RedisClient.Instance.Db.StringGetAsync(ProgressRepository.ReceiptKey(playerId, requestId));
        if (cached.HasValue) return Convert.FromBase64String((string)cached!);
        var journal = CreateGachaJournal(requestId);
        if (!await ProgressRepository.BeginGachaAsync(playerId, journal)) return GachaFailure();
        GachaFaults.ThrowIfEnabled("after-reserve");
        await ProgressRepository.RecoverGachaAsync(playerId);
        return journal.Response;
    }

    private GachaJournal CreateGachaJournal(Guid requestId)
    {
        var pool = state.Characters.Keys.Order().ToArray();
        var draws = Enumerable.Range(0, 10).Select(_ => pool[Random.Shared.Next(pool.Length)]).ToArray();
        var newIds = draws.Distinct().Where(id => !state.Levels.ContainsKey(id)).ToArray();
        var newlySeen = new HashSet<int>();
        long compensation = (10 - newIds.Length) * 100L;
        var result = new byte[53];
        result[0] = 1;
        BitConverter.TryWriteBytes(result.AsSpan(1), (ushort)compensation);
        for (int index = 0; index < draws.Length; index++)
        {
            BitConverter.TryWriteBytes(result.AsSpan(3 + index * 5), draws[index]);
            result[7 + index * 5] = newIds.Contains(draws[index]) && newlySeen.Add(draws[index]) ? (byte)1 : (byte)0;
        }
        return new(requestId, newIds, compensation, state.AcquisitionPower(newIds),
            state.PersonalBoards.Where(pair => newIds.Contains(pair.Value.CharacterId)).Select(pair => pair.Key).ToArray(),
            state.GlobalBoards.Where(pair => newIds.Contains(pair.Value.CharacterId)).Select(pair => pair.Key).ToArray(),
            PacketWriter.Build(PacketId.GachaResult, result));
    }

    private static byte[] BoardResult(bool success) => PacketWriter.Build(PacketId.BoardUnlockResult, [(byte)(success ? 1 : 0)]);
    private static byte[] GachaFailure() => PacketWriter.Build(PacketId.GachaResult, [0]);
}

public static class GachaFaults
{
    public static void ThrowIfEnabled(string point)
    {
        if (ServerConfig.Instance.EnableFaultInjection &&
            Environment.GetEnvironmentVariable("TRICKCAL_GACHA_FAULT") == point)
            throw new InvalidOperationException($"Injected gacha failure: {point}");
    }
}
